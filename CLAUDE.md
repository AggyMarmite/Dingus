# Dingus – notes for working on this repo

## Windows and Linux must always match

Dingus ships as two apps built from this repo:

- **Windows** – WPF (.NET 8): `MainWindow.xaml(.cs)`, `MainWindow.Extras.cs`, `Views/*.xaml(.cs)`, `Services/*.Ui.cs`, `SelfTest.cs`
- **Linux** – Avalonia: everything under `linux/` (`linux/MainWindow.axaml(.cs)`, `linux/Views/*.axaml(.cs)`, `linux/Platform/*`, `linux/SelfTest.cs`)

`Models/`, `Views/ViewModels.cs` and most of `Services/` are shared (see the `<Compile Include>` list in
`linux/Dingus.Linux.csproj`); the screens are written twice.

**Rule: every feature, fix or UI change goes into both apps in the same commit.** A change to a Windows screen
needs the matching change in `linux/` (and the other way round), and a new self-test check on one side gets its
twin on the other. Before committing, look at the diff: if it touches Windows UI files but nothing in `linux/`
(or vice versa), finish the other side first.

The only exceptions are things that genuinely exist on one platform only (e.g. Windows' always-on-top/taskbar
quirks, the Inno Setup installer, AppImage packaging). Say so in the commit message when that's the case.

New shared logic belongs in `Services/` (and the Linux csproj's `<Compile Include>` list) rather than in either
app's screens, so it only has to be written once.

## Releases

- Only publish a release when Kieran says to ("push the update" / "publish"); he may batch changes.
- Release notes (`installer/release-notes.md`, newest section first) are short, vague, high-level bullets.
- Another chat may push to this repo too: always `git pull --rebase` before pushing.
