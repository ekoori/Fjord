# Fjord

A fast, keyboard-first file manager: Vim's modal interaction over a native desktop app.

Built for people who would rather type `d3j` than reach for a mouse, but who still want
progress bars, a trash that works, and a window that behaves like the rest of the desktop.

[![CI](https://github.com/ekoori/Fjord/actions/workflows/ci.yml/badge.svg)](https://github.com/ekoori/Fjord/actions/workflows/ci.yml)

> **Status: early but complete for v1.** Everything on the original v1 list is built.
> Previews, an embedded terminal, git integration and plugins are not. There are no
> releases yet — you build it yourself.

## What works

- **Modal navigation.** `hjkl`, `gg`/`G`, counts (`5j`), `Ctrl-d`/`Ctrl-u`, visual mode.
- **Operators with motions.** `d3j` trashes three rows, `y2j` yanks two. Counts multiply
  the vim way, so `3d2j` covers six.
- **File operations.** Copy, move, rename, duplicate, trash, mkdir, touch — queued,
  cancellable, with progress, and **undoable** with `u`.
- **A real trash.** The Recycle Bin on Windows, the freedesktop spec on Linux.
- **Tabs** with `L`/`H`, and history with `Ctrl-o`/`Ctrl-i`.
- **Registers and marks.** `"ay` collects into `"a`; `mp` remembers a directory and
  `'p` returns to it. Marks survive a restart.
- **A `:` command line** with history and path completion.
- **Fuzzy search.** `/` narrows the listing as you type: `fjc` finds `Fjord.Core.csproj`.
- **Sessions.** Tabs, directories, cursor and selection come back when you reopen.
- **Scrapbooks.** Collect entries from anywhere into a virtual list and act on them together.
- **Live configuration.** Edit the YAML, save, and your keys rebind without a restart.
- **Large directories.** 100,000 entries paints its first screen in about 25 ms.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Nothing else.

```bash
git clone https://github.com/ekoori/Fjord.git
cd Fjord
dotnet run --project src/Fjord.App
```

Runs on Windows, Linux and macOS. CI builds and tests on Windows and Linux; macOS is
untested — see [Platform support](#platform-support).

## Keys

Press `?` in the app for the full list, generated from the command registry so it is never
out of date.

| | |
|---|---|
| `j` `k` | move down / up |
| `gg` `G` | first / last entry |
| `l` `Enter` | open — a directory, or a file in its default application |
| `h` `Backspace` | parent directory |
| `Space` | select, and advance |
| `v` | visual mode |
| `y` `x` | yank / cut — operators, so `y2j` takes three rows |
| `p` | paste |
| `d` | trash — an operator, so `d3j` takes four rows |
| `D` | trash the selection |
| `u` `Ctrl-r` | undo / redo |
| `/` | search — narrows the listing |
| `S` `gs` | add to scrapbook / open it |
| `"a` | use register `a` for the next yank, cut or paste |
| `m` `'` | set a mark here / go to one |
| `L` `H` | next / previous tab |
| `Ctrl-o` `Ctrl-i` | back / forward |
| `?` | help |
| `:` | command line |

Nothing is bound to permanent deletion. `:delete!` is the only route, and it cannot be
undone.

## Configuration

`:config` opens the file in your editor. Saving applies immediately — no restart, no
reload command.

`:set` changes one setting without leaving the keyboard — `:set appearance.font_size 15`,
or `:set appearance.font_size` on its own to see what it is now. Tab completes the names.
Writes go into the file, comments and ordering intact, so what you see next time you open
it is what is actually in effect.

```yaml
appearance:
  font_size: 13
  colors:
    directory: "#8AB4F8"

listing:
  sort_key: name        # name | size | modified | extension
  show_hidden: false

bindings:
  normal:
    "j": "move-down"
    "<Ctrl+d>": "move-half-page-down"
```

The file is generated with every setting documented and every default binding listed as
commented reference, so there is nothing to look up. `:config-path` says where it lives —
`%APPDATA%\Fjord` on Windows, `$XDG_CONFIG_HOME/fjord` on Linux,
`~/Library/Application Support/Fjord` on macOS.

Set `FJORD_CONFIG_DIR` to run an isolated instance without touching your real settings.

## Platform support

| | Build & tests | Trash | Restore from Fjord |
|---|---|---|---|
| Windows | CI | Recycle Bin | no — needs Explorer's own undo stack |
| Linux | CI | freedesktop spec | yes, and from your desktop's file manager |
| macOS | **untested** | `~/.Trash` | yes, but no Finder "Put Back" |

Fjord will not offer an undo it cannot deliver: trashing on Windows simply does not appear
on the undo stack.

## Design

Three decisions shape most of the code, and
[`docs/architecture.md`](docs/architecture.md) explains the rest:

**The file list is drawn, not composed.** No virtualizing panel, no control per row — just
`Render(DrawingContext)` over the visible rows. A frame costs what the window costs, not
what the directory costs, so 100,000 entries costs the same as ten.

**Operations record what they did, not what they were asked to do.** That is what lets undo
survive skipped conflicts, partial failures and cancellation: whatever got as far as being
recorded is exactly what needs reversing.

**Operators resolve their range by running the motion.** `d3j` runs `move-down` for real
and diffs the cursor, rather than modelling what each motion would do. Every motion becomes
a valid operator target the day it is added, and a test enforces that motions stay pure.

## Contributing

See [`CONTRIBUTE.md`](CONTRIBUTE.md). In short: `Fjord.Core` never references Avalonia,
commands are methods with a `[Command]` attribute whose parameters *are* their arguments,
and comments explain **why**.

```bash
dotnet build   # warnings are errors
dotnet test
```

## Licence

MIT — see [`LICENSE`](LICENSE).

Parts of the key-input and command layers were adapted from QuteSharp, a C# port of
[qutebrowser](https://github.com/qutebrowser/qutebrowser), which is GPL-3.0.
[`NOTICE.md`](NOTICE.md) records the attribution and the caveat that comes with it;
[`VENDORED.md`](VENDORED.md) lists every adapted file and every deliberate divergence.
