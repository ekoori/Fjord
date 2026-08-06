# Contributing to Fjord

## Building

```bash
dotnet build
dotnet test
```

.NET 10 SDK, nothing else. Warnings are errors, and `dotnet format` fixes anything the
`IDE0055` formatting rule complains about.

To run against a directory:

```bash
dotnet run --project src/Fjord.App
```

An isolated instance, so experiments cannot damage your real settings:

```bash
FJORD_CONFIG_DIR=/tmp/fjord-dev dotnet run --project src/Fjord.App
```

## Project layout

```
src/Fjord.Core       keys, input modes, filesystem, selection, navigation, seams
src/Fjord.Config     YAML settings, XDG paths, hot reload          -> Core
src/Fjord.Commands   registry, lexer, parser, operator dispatch    -> Core (NOT Config)
src/Fjord.Platform   open, reveal, roots, path comparison          -> Core
src/Fjord.App        Avalonia UI, composition root                 -> all of the above
tests/Fjord.Tests    xUnit + FsCheck
```

**`Fjord.Core` must never reference Avalonia.** That is what lets command bodies, the
input state machine and the whole filesystem layer be tested without a window. If a
command needs something from the UI, it goes behind an interface in `Core` — `IPane`,
`IModeHost`, `IMessageSink` — and the App implements it.

`Fjord.Commands` must not reference `Fjord.Config` either. Config is reached through
`IConfigService`, so `:set` and `:bind` can be tested against a fake.

## Adding a command

A command is a method with a `[Command]` attribute. Its parameters *are* its arguments —
there is no separate table to keep in step:

```csharp
[Command("sort", Group = "Listing", Description = "Change the sort order")]
public void Sort(
    [CommandArgument(Value = CommandValue.Pane)] IPane pane,
    [CommandArgument(Flag = 'k', Choices = ["name", "size"])] string key = "")
```

The registry is read by `:help`, by completion, and by the generated config file, so a
description written here shows up in all three.

To bind it, add a line to `DefaultBindings.All`. A test resolves every binding against the
registry, so a rename that misses a binding fails the build rather than shipping a key
that silently does nothing.

**Motions must be pure cursor moves.** `OperatorDispatcher` resolves `d3j` by running the
motion and diffing the cursor, so a motion with a side effect would apply it twice. A
reflection test enforces this across the registry; if you add a `MotionKind`, it will hold
you to it.

## Adding a setting

Add the property to `FjordConfig`, then add the matching entry to `SettingsSchema.All`. A
test compares the two by reflection, so a setting cannot ship undocumented and a document
cannot describe a setting that no longer exists. The shipped `config.yaml` is generated
from that schema.

## Testing

- Prefer the real thing to a mock. `DirectoryLister` is tested against a real temp
  directory, because its entire value is how it behaves against a real OS — attribute
  mapping, inaccessible entries, entries vanishing mid-enumeration.
- Reach for FsCheck where an invariant matters more than an example: `NaturalComparer`
  being a total order, `SelectionSet.Count` matching its bits, `Split(Quote(t)) == t`.
- A test name is a claim. If the assertions do not actually establish the claim, the test
  is worse than nothing — one here passed for a while while asserting almost nothing, and
  hid a real bug where a cancelled operator stayed armed.

## Style

`.editorconfig` decides. Beyond that:

- Comments explain **why**, not what. If a line needs a comment to say what it does, the
  line is the problem.
- Comment the surprising: the constraint that made this the only option, the failure that
  motivated the guard, the thing the next reader would otherwise "simplify" back into a bug.
- Do not restate parameter names in XML docs; `CS1573` is off precisely so you can document
  only the parameter that is not obvious.
- Constructor injection only, and by hand. There is no container: `MainWindow` builds the
  graph, and a dependency you forgot to supply fails to compile rather than at startup.
- Logging lives at the composition root. The lower assemblies raise events for what is
  worth recording, so a logger is not threaded through logic that tests better without one.
  Never log per directory entry — one record in the transform makes a 100,000-entry
  directory take the better part of a minute.

## Vendored code

Parts of the key, command and input layers came from QuteSharp. `VENDORED.md` records the
provenance and every deliberate divergence. If you change vendored code, add the reason
there — the point is that someone can still diff against upstream.

## Performance

The list is drawn immediately rather than virtualised, so a frame costs what the window
costs, not what the directory costs. Two rules keep it that way:

- Never post work per entry. One coalesced repaint per batch, never per item; 100,000
  dispatcher posts will wedge the queue and starve key input.
- Anything cached per row must be invalidated when the published snapshot arrays are
  *replaced*, not when the row count changes. A re-sort keeps the count and changes
  every row.

Check the enumeration pipeline before and after anything that touches it:

```bash
./scripts/make-benchmark-tree.ps1
dotnet run --project src/Fjord.App -c Release -- --bench bench/flat-100k
```

Targets: first paint < 50 ms, 100k entries < 1.5 s, managed heap < 20 MB.
