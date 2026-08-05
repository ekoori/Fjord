# Fjord architecture

Decisions that are not visible from the code, and the shape reserved for work not yet
written. Update this when a decision here stops being true.

## Project layout

```
src/Fjord.Core       pure logic: keys, input modes, filesystem, selection, navigation, pane seam
src/Fjord.Config     YAML settings, XDG paths, hot reload          (Core)
src/Fjord.Commands   registry, lexer, parser, operator dispatch    (Core, NOT Config)
src/Fjord.App        Avalonia UI, composition root                 (all of the above)
tests/Fjord.Tests    xUnit + FsCheck
```

`Fjord.Core` must never reference Avalonia. That is what keeps enumeration, sorting,
selection and the mode machine testable headlessly and reusable from a future non-GUI
host.

### Projects deliberately not created yet

Empty projects cost restore time on every build and invite premature dependency edges,
so the shape is recorded here instead of stubbed in the solution.

| Project | Milestone | Contents |
|---|---|---|
| `Fjord.Platform` | M1 step 13 | `IPlatformServices`: roots, open, reveal, path comparison |
| `Fjord.Operations` | M2 | Copy/move/delete, undo log, Channel-based operation queue |
| `Fjord.Storage` | M3+ | SQLite: bookmarks, recents, sessions, scrapbooks, FTS index |
| `Fjord.Preview` | M4 | Preview plugins; PDF reuses `NoPdf.Core` |
| `Fjord.Terminal`, `Fjord.Git`, `Fjord.Plugins` | later | as named |

`Fjord.Commands` must **not** reference `Fjord.Config`. `IConfigService` lives in Core and
is implemented in Config, so `:set` and `:bind` can be tested against a fake.

One `Fjord.Platform`, not three per-OS assemblies. With `ProjectReference` all three would
be referenced on every OS anyway; with dynamic loading, single-file publish and trimming
break. `OperatingSystem.IsWindows()` is special-cased by ILLink, so a RID-specific publish
trims the dead branches for free. OS-specific members carry `[SupportedOSPlatform]`, and
`TreatWarningsAsErrors` turns CA1416 into a hard build error — stronger enforcement than
three assemblies would give.

## Divergences from the project spec

**Config is YAML (YamlDotNet 18.1.0), not TOML.** Chosen for consistency with the sibling
projects noPDF and QuteSharp, which both use YamlDotNet.

The anticipated cost was the binding table: YAML 1.1 reads unquoted `n`, `y`, `on` and
`off` as booleans, and `n` (next match) and `y` (yank) are core vim bindings. **Measured,
this does not bite Fjord's own reader** — `Bindings` is typed
`Dictionary<string, Dictionary<string, string>>`, so YamlDotNet takes each key's scalar
text and never applies boolean inference. `UnquotedBooleanLookingKeysAlsoSurvive` pins
that, and will start failing if the map is ever loosened to an untyped shape for plugin
settings — at which point unquoted keys really would become lossy.

Generated files still quote every binding key. Two reasons that survive the above: keys
like `:`, `#`, `{`, `&a` and `<Ctrl+a>` need quoting to be valid YAML at all, and other
tools pointed at the file (yq, editor YAML modes) *do* apply boolean inference, so a bare
`n:` misrepresents the file to everything except Fjord.

**Storage is its own project, not part of Core.** The spec's project list omits one.
Putting SQLite in Core would drag `SQLitePCLRaw` native assets into the pure-logic
assembly.

## The file list is drawn, not composed

`FileListView` is a single `Control` that overrides `Render(DrawingContext)`. There is no
`ListBox`, no `ItemsRepeater`, no `VirtualizingStackPanel`.

The cost of a frame is set by the size of the window, not the size of the directory:
100,000 entries costs exactly what 10 does. Supporting details:

- `ILogicalScrollable` with `IsLogicalScrollEnabled => true`. False would make
  `ScrollContentPresenter` size the child to the extent — 2.2 million DIP tall for 100k
  rows, past where Skia's single-precision coordinates stay exact. Implementing the
  interface rather than hand-rolling a scrollbar is what buys wheel, trackpad inertia and
  Fluent chrome.
- A 256-slot `FormattedText` ring cache keyed by `row % 256` plus a generation counter.
  Building formatted text runs bidi, itemisation, font fallback and shaping; rebuilding
  every visible row every frame would spend a third of the frame budget redrawing
  identical text. A theme, scaling or sort change bumps the generation rather than
  clearing the array.
- Row height derives from font metrics and is snapped to a whole device pixel. Unsnapped,
  at 150% scaling every other baseline lands on a half pixel and the list reads as blurry.
- Row backgrounds are coalesced into runs, so a full-viewport visual selection is one
  fill rather than forty.
- Cached rows are dropped whenever the published snapshot arrays are *replaced*, never on
  a count comparison. A re-sort or filter change rewrites every row's content and leaves
  the count identical; keying on count made the list draw stale text in the new order's
  positions. `SnapshotView.IsSameAs` is that test.
- `FormattedText.MaxLineCount = 1`. Without it, text too wide for its column wraps into
  the row below instead of trimming — invisible until a font size or column width makes
  something overflow.
- Column widths scale with `font_size`, so raising it widens the columns rather than
  truncating their contents.

### Accepted debt

**No `AutomationPeer` and no text selection.** Both come free with a `ListBox`. A custom
virtualized automation peer is required before Fjord can claim accessibility — this is
known debt, not an oversight.

## Threading and publication

`DirectorySnapshot` is written by exactly one producer thread and read by any number of
readers. Published arrays are never mutated; an update builds new ones and swaps them in.

The entry array and the sorted view array are published **together** as one immutable
record. Reading them separately could pair a new view with an older, shorter entry array
and index out of range. `SnapshotView` is the captured pair; the renderer takes one per
frame with a single volatile read.

New batches are sorted among themselves and then **merged** into the existing view, not
binary-search-inserted. Insertion would shift the tail of a 100,000-element array 2,048
times per batch.

**Invalidation is coalesced.** `FileListView.RequestRedraw()` is safe from any thread and
collapses any number of requests between two frames into one dispatcher post. Posting per
arriving item would put 100,000 work items on the dispatcher queue and starve key input —
the app would feel frozen while technically making progress. This is the single most
likely way to miss the performance targets.

## Metadata is optional in the fast path

`ListingOptions.NeedsMetadata` is derived from the active sort and the visible columns.
On Windows, size and timestamps arrive in the directory-read buffer for free. On Unix both
are served by one lazy `stat` per entry that .NET caches — so asking for one or both costs
the same, but asking for neither costs nothing. On a network mount that difference is the
entire cost of a listing.

Background metadata must be keyed by `SnapshotView.SourceIndex` (arrival order), never by
row. A resort or filter change renumbers every row, and results arriving afterwards would
land on the wrong entries.

## Command-line quoting is path-shaped, not shell-shaped

Fjord's command arguments are overwhelmingly filenames, so the lexer's escaping rules were
chosen for paths rather than inherited wholesale:

- Inside double quotes **only `\"` escapes**. Not `\\` — treating that as an escape turns
  `"\\server\share"` into `\server\share`, and a UNC path is far likelier than someone
  wanting a literal backslash pair. QuteSharp escapes every character after a backslash,
  which silently mangles `"C:\Users\igor\a b.txt"` into `C:Usersigora b.txt`.
- **Single quotes escape nothing**, so they carry any path verbatim. `CommandLexer.Quote`
  emits them by default, falling back to alternating runs (`'don'"'"'t'`) for tokens
  containing a single quote, relying on the lexer concatenating adjacent quoted sections.
- A double-quoted string still cannot end with a backslash. That ambiguity is inherent to
  `\"` escaping — bash has it too — and single quotes are the way to write it.

## Key routing

`AddHandler(KeyDownEvent, ..., RoutingStrategies.Tunnel)` on the window, so the mode
manager sees a key **before** whatever has focus. Without tunnelling, Escape could not
leave the command line once it has a `TextBox`.

`InputController` is the only join between the three layers: `AvaloniaKeys` translates the
event, `ModeManager` decides what it means, `CommandParser` runs it. Everything modal
lives in Core.

Two things are resolved before the mode manager, because they read a raw keystroke rather
than matching bindings: `KeyMode.Register` (the key after `"`), and Escape — which must
work from any mode, including ones that would otherwise forward an unmatched key to a text
field.

Async command results are observed, not dropped: a navigation command that fails on
permissions reports it rather than leaving the pane looking merely empty.

## Mode commands go through `IModeHost`

Entering visual mode and quitting need the mode manager and the desktop lifetime, both of
which live in the App layer — so the obvious place for those commands is the App layer too.
They live in `Fjord.Commands` behind `IModeHost` instead, for one reason: the test that
resolves *every* default binding against the command registry only guards commands the
test project can see. App-layer commands would be invisible to it, and a rename could ship
a key that silently does nothing.

## File operations

Operations are objects that **record what they actually did**, not what they were asked to
do. That one decision is what makes undo survive skipped conflicts, partial failures and
cancellation: whatever got as far as being recorded is exactly what needs reversing, so a
copy cancelled at item seven of twenty is cleaned up by a single `u`.

A cancelled copy also discards its partial write. `CopyToAsync` has already created the
destination and written part of it, and the operation never reaches the line that records
it — so undo could not clean it up and the user was left with a **truncated file that looks
real**. That is data loss, not untidiness. A test caught it.

**The queue is sequential on purpose.** A bounded worker pool is right for metadata and
thumbnails, which are mostly waiting. Bulk file I/O is not: two concurrent copies on one
disk are slower than one after the other, and they make progress reporting meaningless and
conflict prompts race. Cancellation is per operation, so stopping a copy leaves what is
queued behind it alone.

Conflicts default to **fail** — the only policy that cannot lose data — with skip,
overwrite and rename-alongside available. The App picks rename, because paste and duplicate
land in a directory that often already holds the name and refusing would make the common
case fail.

### Trash, and undo you can actually deliver

`dd` trashes; only an explicit `:delete!` deletes permanently, and a test asserts that
nothing is bound to it. Permanent deletion reports `CanUndo => false` rather than sitting
on the undo stack unable to help.

The platforms differ in a way the user can see:

| | trashes | Fjord can restore | verified |
|---|---|---|---|
| Windows | `SHFileOperationW` + `FOF_ALLOWUNDO` | **no** — needs Explorer's own undo stack | manually, items confirmed in the Recycle Bin |
| Linux | freedesktop spec, incl. `.Trash-$uid` on other volumes | yes, and so can the desktop | `XdgTrashServiceTests`, run on Ubuntu 24.04 |
| macOS | `~/.Trash` | yes, but no Finder "Put Back" | **not verified** — no machine available |

`XdgTrashServiceTests` runs the real service against a redirected `XDG_DATA_HOME`, so it is
self-contained and never touches the developer's own trash. It covers the `.trashinfo`
format, restore-and-consume, same-name collisions from different directories, whole
directory trees, and the percent-encoding of spaces and Unicode. Those tests skip
themselves off Linux rather than passing vacuously — a platform test that silently passes
everywhere is worse than none.

macOS remains the gap. It will move items to `~/.Trash` and Fjord's own undo works, because
`TrashOperation` remembers where each item came from; what it will not have is the Finder's
"Put Back", which needs `NSFileManager.trashItemAtURL` through Objective-C interop.

`TrashOperation.CanUndo` is false unless the platform handed back a token for every item,
so on Windows a trash simply does not appear on the undo stack. Advertising an undo that
would silently do nothing is worse than not offering one.

## The comparator is the hot path

Merging 100,000 entries into sorted order runs a few million comparisons, so
`NaturalComparer` sets the cost of loading a large directory more than the enumeration
does. Two things matter, both measured:

- **Compare characters before folding case.** In a sorted merge the names share long
  prefixes, so the overwhelmingly common outcome is "identical character".
- **`char.ToUpperInvariant` has an ASCII fast path.** It otherwise goes through the Unicode
  casing tables on every character.

Together these took a 100,000-entry listing from **2,130 ms to 432 ms** — throughput from
47k to 232k entries/sec. Nothing else in the pipeline was touched. Anything non-ASCII still
falls through to the real implementation, so the ordering is unchanged; the total-order
property tests are what make that safe to assert.

## Tabs

Each tab owns its pane, and each pane its own snapshot and cancellation source, so a switch
is a pointer swap: it does not touch, cancel or await background work, and a listing started
in a tab you left keeps running into that tab's own snapshot.

There is **one `FileListView` for the whole window**, re-pointed on each switch — twenty
tabs each holding a row-text cache would be waste. The consequence is that an inactive pane
cannot leave its cursor, selection and scroll position on the view; `PaneController` holds
them while detached and hands them back through `Adopt`. `IsActive` is what decides which
of the two the `IPane` members read.

## The platform seam is one project, not three

With plain project references all three platform assemblies would be referenced on every OS
anyway, and dynamic loading breaks single-file publish and trimming. `OperatingSystem.IsWindows()`
is special-cased by the linker, so a RID-specific publish proves the other branches dead and
drops them. `[SupportedOSPlatform]` plus `TreatWarningsAsErrors` turns CA1416 into a build
error, which is stronger enforcement than separate assemblies.

Two platform details that cost time if rediscovered:

- Windows: `explorer /select,"path"` — the comma with no space is required, and explorer
  returns exit code **1 on success**, so nothing may check it.
- Linux: `org.freedesktop.FileManager1.ShowItems` over D-Bus is the only way to get the file
  *selected*; `xdg-open` on the parent is the fallback when no session bus is available.

## Help and configuration

`:help` renders **in the app**, `:config` opens the YAML **outside** it. The split is
deliberate:

- The question a user asks help is almost always "what key does this". Having to leave a
  keyboard-driven file manager to find that out is precisely the wrong answer, so the
  overview is a scrollable overlay bound to `?` and `F1`.
- Configuration is text editing, which the user already has a tool for. Fjord watches the
  file and hot-reloads it, so saving in that editor rebinds keys immediately — an in-app
  editor would be a worse version of something they have already configured.

`HelpText` is a pure function of the registry and the bindings, so it cannot drift from
what the app actually does — the same reason the config file is generated rather than
hand-written. It **inverts** the bindings: they are stored keys-to-command, and the useful
direction is the opposite. Keys from a non-normal mode are labelled `(v)`, because `y` in
visual mode and `y` in normal mode are genuinely different bindings.

`IConfigService` is deliberately narrow — path, ensure-exists, reload. The plan sketched a
`Get`/`Set`/`Bind` surface, but `:set` and `:bind` do not exist yet and designing their
shape before there is a caller would be guessing. It exists at all so `Fjord.Commands` can
offer `:config` without depending on `Fjord.Config`.

## The command line

`CommandLine` owns its own `TextBox` and handles Enter/Tab/Up/Down/Escape locally rather
than through the mode manager — once the box has focus the user is typing text, and only
those few keys mean anything else. The window's tunnelling handler bails out while it is
open, or every letter typed would be matched against normal-mode bindings first.

Two things that are easy to get wrong and are pinned by tests:

- **Cycling re-applies to the original line, never to the current text.** A
  `CompletionResult`'s span describes the line it was computed from; applying the second
  candidate to what the first produced overwrites the wrong characters (`:cd s` cycled
  twice yields `:cd src/cripts/`).
- **Completion offers flag *values*, not just positional arguments.** `:sort -k <tab>` is
  completing the choices declared on `--key`, and `sort` has no positional arguments at
  all — counting positions alone finds nothing.

Path completion emits `/` as its separator even on Windows. .NET accepts it everywhere, and
a backslash would force `CommandLexer.Quote` to wrap every candidate in quotes, turning
`:cd scripts/` into `:cd 'scripts\'`.

Fluent's `TextBox` template paints a `PART_BorderElement` regardless of the control's own
`Background` and `BorderThickness`, so the focused command line gets a bright accent box
around it. Restyling that part is the only way to defeat it; QuteSharp needed the same
override.

## The window clamps itself to the screen

`Width`/`Height` are device-independent pixels, and the usable desktop can be smaller than
the default 1000x700: a 2560x1440 screen at 250% scaling offers 1024x576 DIP. The overflow
lands at the bottom — exactly where the status bar and command line live — so the app looks
like it has neither. `ClampToScreen` on `Opened` shrinks and re-centres.

## Operators resolve their range by running the motion

`OperatorDispatcher` handles `d3j` by running `move-down` for real and diffing the cursor,
then restoring it, rather than maintaining a parallel model of what each motion would do.
Every motion becomes a valid operator object the day it is added, and the two can never
disagree.

This is only sound because motions are pure cursor moves. That is enforced, not hoped: a
test reflects over the registry and asserts every command declaring a `MotionKind` leaves
the snapshot untouched.

## Long paths on Windows

`app.manifest` sets `longPathAware`. This is necessary but not sufficient — the machine
also needs `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled`. Without
both, enumerating a deep tree throws `PathTooLongException` from inside the enumerator and
the pane simply looks empty with nothing to explain why.

`scripts/make-benchmark-tree.ps1` builds `bench/deep` with 434-character paths using the
`\\?\` prefix, because Windows PowerShell 5.1 is not itself long-path aware. That prefix
disables all path normalisation, so paths given to it must already be absolute and
backslash-separated.

## No XAML

There is no `App.axaml`. Avalonia's generated XAML partial classes do not satisfy
`TreatWarningsAsErrors`, and the usual workaround is to disable it for the UI project —
which is where UI code most benefits from it. With the list drawn immediately and the
chrome built in code, markup buys nothing.

`GenerateDocumentationFile` is on solely so IDE0005 (unused using) runs at build time.
CS1591 and CS1573 are suppressed: the house style documents what is surprising, not every
member and parameter.

## Performance targets

Measured by `Fjord.App --bench <dir>` against trees from `scripts/make-benchmark-tree.ps1`.

| Metric | Target | Measured (100k, Release, NTFS) |
|---|---|---|
| Time to first batch | < 50 ms | 31.6 ms |
| Full enumeration of 100k | < 1,500 ms | 808 ms |
| Managed heap after 100k | < 20 MB | 12.8 MB |
| Steady-state `Render` | < 3 ms at 1080p | not yet instrumented |

The ~30 ms floor on tiny trees is first-call JIT inside the measured region, not
enumeration; it is amortised in the running app.

Frame time needs the in-app frame-stats overlay (`:set debug.frame-stats true`) before it
can be claimed. Until then the `Render` target is unverified.

## Vendored code

See [VENDORED.md](../VENDORED.md). The key/mode layer is copied from QuteSharp rather than
referenced, because QuteSharp pins `net8.0` for its vendored CEF stack.
