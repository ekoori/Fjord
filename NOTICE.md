# Notices and attribution

Fjord is distributed under the MIT License (see `LICENSE`).

## Third-party code

### QuteSharp and qutebrowser

Parts of Fjord's key-input and command layers were adapted from
[QuteSharp](https://github.com/ekoori), a C# port of
[qutebrowser](https://github.com/qutebrowser/qutebrowser) 3.7.0. `VENDORED.md` lists
exactly which files, and every deliberate divergence from them.

The affected areas are the key model (`Key`, `KeyInfo`, `KeySequence`), the modal input
layer (`KeyBindings`, `ModeManager`), the Avalonia key bridge, and the command
registry/lexer/parser. Some test tables were adapted from qutebrowser's own
`tests/unit/keyinput/test_keyutils.py`.

**qutebrowser is licensed under the GNU General Public License v3.0 or later.** Its
copyright is held by Florian Bruhin and contributors.

> Fjord is published under MIT at the copyright holder's direction. Anyone
> redistributing Fjord, or relying on its licensing, should satisfy themselves about the
> status of the adapted portions above — GPL-3.0 is a copyleft licence, and this
> combination has not been reviewed by a lawyer. `VENDORED.md` identifies the specific
> files so the question can be evaluated, or those files replaced.

### noPDF

Patterns — not code — were taken from noPDF: the debounced configuration watcher, the
async render-and-cancel shape, and the immediate-mode `Render(DrawingContext)` approach
used by the file list.

## Dependencies

| Package | Licence |
|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | MIT |
| [YamlDotNet](https://github.com/aaubry/YamlDotNet) | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT |
| [xUnit](https://github.com/xunit/xunit) | Apache-2.0 |
| [FsCheck](https://github.com/fscheck/FsCheck) | BSD-3-Clause |
| [Xunit.SkippableFact](https://github.com/AArnott/Xunit.SkippableFact) | MS-PL |
