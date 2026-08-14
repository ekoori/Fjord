# Vendored code

Code copied from another project rather than referenced, with what changed and why.
Keep this file current — the point is that a future reader can diff against upstream.

## QuteSharp key-input layer

**Source:** QuteSharp, a C# port of
[qutebrowser](https://github.com/qutebrowser/qutebrowser) 3.7.0, taken from a local working
copy that had no public remote when this code was copied. The immediate source therefore
cannot be linked, and anyone wanting to diff should go to qutebrowser itself: the parsing
semantics and the test tables come from `qutebrowser/keyinput/` and
`tests/unit/keyinput/` there, and this code is three generations from that origin.

**Why copied rather than referenced:** every QuteSharp project pins `net8.0` to match its
vendored CefGlue stack, so a `net10.0` project cannot reference them. Fjord also has to
extend the mode model with Visual and operator-pending, which QuteSharp has no concept
of, so the code was always going to diverge.

### Files

| Fjord | QuteSharp origin |
|---|---|
| `src/Fjord.Core/Keys/Key.cs` | `src/QuteSharp.Core/Keys/Key.cs` |
| `src/Fjord.Core/Keys/KeyInfo.cs` | `src/QuteSharp.Core/Keys/KeyInfo.cs` |
| `src/Fjord.Core/Keys/KeySequence.cs` | `src/QuteSharp.Core/Keys/KeySequence.cs` |
| `src/Fjord.Core/Input/KeyBindings.cs` | `src/QuteSharp.KeyInput/KeyBindings.cs` |
| `src/Fjord.Core/Input/ModeManager.cs` | `src/QuteSharp.KeyInput/ModeManager.cs` |
| `tests/Fjord.Tests/Keys/KeySequenceTests.cs` | `tests/QuteSharp.UnitTests/Keys/KeySequenceTests.cs` |
| `tests/Fjord.Tests/Input/ModeManagerTests.cs` | `tests/QuteSharp.UnitTests/KeyInput/ModeManagerTests.cs` |

`src/Fjord.Core/Input/KeyMode.cs` is **not** vendored — it replaces QuteSharp's
`src/QuteSharp.KeyInput/KeyMode.cs` outright.

### Changes made during the copy

1. **Namespaces** `QuteSharp.Core.Keys` → `Fjord.Core.Keys`, `QuteSharp.KeyInput` →
   `Fjord.Core.Input`. `KeyInput` is folded into `Core` because, once
   `DefaultBindings.cs` is dropped, it is ~430 LOC with no dependencies.

2. **`KeyMode` is a `readonly record struct` wrapping a name, not a closed enum**, and
   the per-mode policy predicates (`ForwardsUnmatchedKeys`, `SupportsCount`, plus a new
   `IsSelectionMode`) moved from `switch` expressions in a static `KeyModes` class into
   a data table on `KeyModeRegistry`. Fjord needs Visual and operator-pending modes
   immediately and will let plugins add modes later. `default(KeyMode)` deliberately
   reads as `normal` so it is safe as a dictionary key.

3. **`KeyBindings.Match` no longer rebuilds the merged binding map per keystroke.**
   Upstream calls `For(mode)` inside `Match`, which allocates a `Dictionary` and inserts
   every binding in the mode on *every key press*, then linear-scans it. The merged view
   is now cached per mode and invalidated on `AddDefault`/`Bind`/`Unbind`/`BindDefault`,
   and is additionally indexed by first key so a match scans only the handful of
   bindings that could possibly apply. `ClearUserBindings()` is new, for config reload.

4. **`ModeManager` takes a `KeyModeRegistry`** alongside `KeyBindings`, and `EnterMode`
   throws on an unregistered mode so a mode-name typo fails where it happens rather than
   silently swallowing every subsequent key.

5. **`DefaultBindings.cs` was not copied.** It is the only thing that made the key layer
   depend on QuteSharp's config assembly, and it reads qutebrowser's `configdata.json`
   schema, which Fjord does not have. Fjord generates its default bindings from the
   command registry instead (see the plan, §6).

6. **Browser-specific commentary removed**; `Key.Back`/`Key.Forward` are kept because
   mice with side buttons report them and binding them to navigation history is useful.

7. **Test command strings renamed** to file-manager terms (`scroll down` → `move-down`).
   They are opaque tags in these tests; every assertion is structurally unchanged.

8. **`DefaultBindingsTests` was not ported** — it exercises qutebrowser's shipped binding
   set through QuteSharp's config schema. Fjord's equivalent arrives with its own
   default-bindings table (plan step 9), including the "every default binding resolves
   against the command registry" test.

## QuteSharp command infrastructure

**Source:** the same QuteSharp working copy, `src/QuteSharp.Commands/`.

| Fjord | QuteSharp origin |
|---|---|
| `src/Fjord.Commands/CommandAttributes.cs` | `CommandAttributes.cs` |
| `src/Fjord.Commands/CommandDefinition.cs` | `CommandDefinition.cs` |
| `src/Fjord.Commands/CommandNaming.cs` | `CommandNaming.cs` (verbatim) |
| `src/Fjord.Commands/CommandLexer.cs` | `CommandLexer.cs` |
| `src/Fjord.Commands/CommandRegistry.cs` | `CommandRegistry.cs` |
| `src/Fjord.Commands/CommandParser.cs` | `CommandParser.cs` |

`src/Fjord.Commands/OperatorDispatcher.cs` is new — QuteSharp has no operator-pending
concept. `src/Fjord.Core/Panes/IPane.cs` replaces `IBrowserTab.cs`, whose `IMessageSink`
was kept and moved to `src/Fjord.Core/Diagnostics/`.

### Changes made during the copy

1. **`CommandValue` retargeted.** `WindowId` and `Tab` deleted; `Pane`, `Workspace`,
   `Register`, `Range` and `Messages` added. `CommandContext` carries the same.

2. **Commands can declare themselves operators or motions.** `IsOperator` and
   `Motion` (`MotionKind`) are new on `CommandAttribute`, along with `Group` for `:help`.

3. **Backslashes inside double quotes are no longer general escapes.** This is the most
   consequential divergence and it fixes a real defect for this domain. QuteSharp escapes
   any character after a backslash, because its arguments are URLs and JavaScript. Fjord's
   are Windows paths, and that rule silently turns `"C:\Users\igor\a b.txt"` into
   `C:Usersigora b.txt` — mangled, with no error. Now only `\"` is an escape.
   **`\\` is deliberately not one either**: treating it as an escape breaks UNC paths,
   turning `"\\server\share"` into `\server\share`.

4. **`Quote` is new, and emits single quotes.** Single quotes escape nothing, so any path
   survives verbatim. A token containing a single quote is emitted as alternating runs
   (`'don'"'"'t'`), relying on the lexer concatenating adjacent quoted sections — the same
   trick a POSIX shell uses. A double-quoted string still cannot end with a backslash;
   that ambiguity is inherent to `\"` escaping and bash shares it.

5. **`ParsedCommand.Invoke` unwraps `TargetInvocationException`.** Reflection otherwise
   surfaces that to the user instead of what actually went wrong.

6. **`Supply` fails loudly on missing context.** A command declaring a range but dispatched
   without one is a caller bug; it now raises a `CommandException` naming what was missing
   rather than passing null into the command body.

7. **`CommandParser.Execute`** added — parse-and-run, which every caller wanted.

### Not yet ported

- `src/QuteSharp.App/Input/AvaloniaKeys.cs` — arrives at plan step 9. Copy it verbatim;
  its `KeySymbol` handling for punctuation is the reason `:` works on a US layout.
- `Completion/*`, `ReadlineCommands.cs`, `CommandHistory.cs` — arrive with the command
  line at plan step 10.

## noPDF single-instance forwarding

**Source:** `C:\Users\igor\dev\nopdf`, `src/NoPdf.App/SingleInstance.cs`.

`src/Fjord.App/SingleInstance.cs` is the same idea and the same named-pipe mechanism,
rewritten rather than copied: a second launch connects to a pipe named for the current
user, writes its paths, and exits; the first launch serves that pipe on a background
thread.

noPDF needs it so a second PDF opens as a tab. Fjord needs it for a stronger reason —
without it Fjord cannot be a file manager at all, because every double-clicked folder
starts a process and each one would be its own window.

### Changes made during the copy

1. **Typed catches.** The original catches everything, in both the client and the server
   loop. Here the client catches `TimeoutException`, `IOException` and
   `UnauthorizedAccessException` — the three ways "nobody is listening" actually arrives —
   and the server catches the two that a half-open pipe produces. Anything else is a bug
   and should not be swallowed by the thread that keeps the window reachable.

2. **The escape hatch lives in the class.** noPDF checks `NOPDF_NO_SINGLE_INSTANCE` at the
   call site in `Program`, which means the server thread starts anyway and the isolated
   instance still claims the pipe — so the *next* launch forwards into it. `IsDisabled` is
   checked by both `TryForward` and `Listen`, so an isolated instance neither forwards nor
   is forwarded to.

3. **Switches are filtered out before forwarding.** `Program.PathsIn` drops anything
   starting with a dash, so `--config-path` reaching a running window is not opened as a
   folder by that name.
