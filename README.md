# Dingus

A lightweight, modern IPTV player for Windows 10/11, Linux and macOS (.NET 8 and the VLC video engine; WPF on Windows, Avalonia on Linux and Mac).

## Build & run

You need the **.NET 8 SDK** (one-time install):

```powershell
winget install Microsoft.DotNet.SDK.8
```

Then, from the folder that contains `Dingus.csproj`:

```powershell
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

To make a stand-alone folder you can copy anywhere (run `publish\Dingus.exe`):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

Add `--self-contained true` if the PC you copy it to doesn't have the .NET 8 runtime.

## Download

Ready-made installers are on the [Releases page](https://github.com/AggyMarmite/Dingus/releases).
Download `Dingus-Setup-x.y.z.exe` and run it. It installs for your Windows account only, so it doesn't need admin
rights, and it adds Start menu and (optionally) desktop shortcuts.

### Linux

Download `Dingus-x.y.z-x86_64.AppImage` from the same page, make it executable and run it:

```bash
chmod +x Dingus-*-x86_64.AppImage
./Dingus-*-x86_64.AppImage
```

The AppImage carries everything it needs, including its own copy of VLC, so nothing else has to be installed. It runs on
most 64-bit distributions from 2022 onwards (Ubuntu 22.04+, Fedora 36+, Debian 12, Mint 21+, …). If your system can't
run AppImages directly, install `libfuse2` (Ubuntu/Debian: `sudo apt install libfuse2`), or run it with
`--appimage-extract-and-run`. Dingus updates itself from the releases page, as on Windows.

The Linux version has the same pages and player as the Windows one. Not yet on Linux: media keys / desktop media
controls, adaptive frame rate, battery-aware mode and the colour themes.

To build it yourself (needs the .NET 8 SDK and VLC: `sudo apt install vlc libvlc-dev`):

```bash
dotnet run --project linux/Dingus.Linux.csproj
linux/package/build-appimage.sh 0.9.0 out    # builds the AppImage (on Ubuntu 22.04)
```

### Mac

Download the disk image for your Mac from the same page:

* **Apple Silicon** (M1 and later): `Dingus-x.y.z-mac-arm64.dmg`
* **Intel**: `Dingus-x.y.z-mac-x64.dmg`

Open it and drag **Dingus** into **Applications**. Needs macOS 12 (Monterey) or later; VLC is built in.

Dingus isn't signed with a paid Apple Developer ID, so the first time you open it macOS says it can't check it for
malicious software. Open **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to the
message about Dingus (on older macOS: right-click Dingus in Applications → **Open**). You only need to do this once,
and updates installed from inside Dingus don't ask again.

The Mac version is the same app as the Linux one, with the usual red/yellow/green window buttons, the Dingus menu
(⌘, for Settings) and ⌘ in place of Ctrl for shortcuts. Not yet on the Mac: media keys / Control Centre's Now Playing,
adaptive frame rate, battery-aware mode and the colour themes.

To build it on a Mac (needs the .NET 8 SDK; VLC is downloaded by the script):

```bash
linux/package/build-mac.sh 0.9.0 arm64 out   # or x64 for Intel
```

### Making a release

Releases are made by hand: raise `<Version>` in `Dingus.csproj`, update `installer/release-notes.md`, push, then
run **Actions → Release → Run workflow**. It builds the Windows installer, the Linux AppImage and the two
Mac disk images and attaches them all. Pushing to main on its own only builds and tests (all three versions).

## What's in this build

- **TV Guide** – Classic grid with channel logos, wrapped channel names, a live mini player,
  now/next details, a red "now" line, earlier/later/now navigation, favourites (right-click a channel)
  and a category rail. Search covers every channel, not just the selected category.
- **EPG auto-matching** – guide channels are matched by EPG id first, then by normalised name
  ("UK: Channel One HD" ↔ "Channel One"), so most channels line up without manual work. Missing logos fall back to the guide's icons.
- **Films / TV Shows** – virtualised poster grids, categories, sort (recently added, A–Z, top rated),
  detail pages with plot/cast/backdrop, seasons and episodes.
- **Full-screen player** – double-click the mini player (or "Watch full screen"). Auto-hiding controls,
  seek bar for films, live programme progress for channels, audio track and subtitle pickers.
- **Settings** – multiple named playlists (Xtream Codes or M3U), account status/expiry/connections,
  refresh playlist and guide, auto-refresh interval, custom EPG URL, guide time shift, hide categories,
  hardware decoding, stream format and buffer size, image cache.

### Keyboard

| Key | Action |
|---|---|
| 0–9 | Type a channel number (Guide or live player) – it tunes after a moment, or press Enter |
| 0 / Backspace | Back to the last channel you watched |
| Esc | Back to the guide (from the player or the mini player) |
| Enter / L | Channel list in the player – what's on now; ↑↓ choose, ←→ category, Enter watch |
| F / F11 / Esc / double-click | Leave full screen (back to the guide) |
| P | Picture-in-picture (always on top; drag to move, snaps to corners; resize with the corner grip or Ctrl + wheel) |
| V | Multi-view (4 / 6 / 8 channels at once) |
| I | Pin stream info (resolution, frame rate, bitrate, dropped frames) |
| Space | Play / pause |
| ↑ ↓ | Live TV: info bar – see what's on other channels without changing, Enter to watch · films: volume |
| PgUp / PgDn | Channel up / down |
| ← → | Back 10 s / forward 30 s (films) · back / forward 10 s (live TV) |
| End | Live TV: back to live |
| Home | Live TV: start of the programme (catch-up channels) |
| Ctrl + ← → | Live TV: back / forward 5 minutes |
| S | Sleep timer |
| R | Record this programme |
| Ctrl + F | Search everything |
| ? / F1 | Keyboard shortcut list |
| Home | Start a film or episode from the beginning |
| N | Next episode |
| M | Mute · mouse wheel = volume |
| Media keys | Play/pause, stop, next/previous work even when the app isn't focused (while something plays) |

### Watching

- **Home**: a big featured picture across the top that moves on every few seconds – carry on watching (your last channel),
  films and episodes you're part-way through, what's on your favourites, and new films and shows – with Watch / More info
  buttons. Below: favourite channels (each on a tile in its logo's colour), continue watching, coming up next, live sport,
  your reminders, tonight's picks, recently watched, new films and new TV shows. Nothing plays until you press Watch.
  (Settings → TV Guide → "Open on the TV Guide" to start on the guide instead.)
- Opens on the channel you last watched; **Recently watched** is in the guide's categories.
- **Continue watching** in Films and TV Shows; films and episodes resume where you stopped.
  At the end of an episode, the next one starts after a 10-second countdown.
- **Catch-up** (Xtream providers that offer it): a ⟲ mark on guide programmes – double-click one,
  or use "Start over" / "Watch catch-up" on the programme details.
- **Stream health**: if a stream stalls or errors, it reconnects automatically (up to 3 tries).
- **Merged channels**: "Channel One", "Channel One HD", "Channel One FHD" and backups show as one channel; if a version
  fails the next is tried automatically, and the player's quality button lets you pick a version.
- **On now / Tonight**: buttons above the guide's categories switch to a list of what's on now or this evening,
  colour-coded by genre (sport, film, news, kids, factual, entertainment).
- **Battery-aware mode**: dedicated graphics when plugged in, built-in graphics on battery (with 1080p instead of
  4K for merged channels and no background refreshes).
- **Multi-view** (V, or the button in the player / guide): 4, 6 or 8 channels at once. Click a screen for its sound,
  Enter to change channel, F for full screen, Del to remove. Warns when your plan's connection limit can't cover them all.
- **Picture-in-picture** (P): a floating, always-on-top window. Drag to move (snaps into corners), drag the corner grip
  or Ctrl + mouse wheel to resize, double-click for full screen.
- **Taskbar controls**: previous / play-pause / next buttons on the taskbar thumbnail, and film progress on the icon.
- **Catch-up on the live timeline** (Xtream providers with catch-up): drag the bar under the live picture, or press
  ← / → (Ctrl for 5 minutes), Home for the start of the programme and End for live. Earlier than the rewind buffer,
  Dingus switches to the provider's catch-up automatically and plays on up to live. "Start over" / "Watch catch-up"
  in the guide land on the same timeline.
- **Pause & rewind live TV**: Space pauses, ← goes back 10 s, End catches up. The channel is buffered to disk
  (30 min – 2 h, never more than a quarter of your free space) through one connection, so memory use stays flat.
  HLS streams play directly without it.
- **Sleep timer** (S, or the moon in the player): 15 / 30 / 60 / 90 minutes or the end of the programme / film.
- **Reminders**: right-click a future programme (guide, lists, Home) or use "Remind me" – a pop-up with a Watch button
  appears a minute before it starts. Your reminders are on Home.
- **Find a programme**: in the guide, "Find a programme" searches every channel's listings by title.
- **Recordings & reminders** (Recordings tab): right-click a programme → Record (or R / the record button in the player)
  and it's saved as a .ts file to a folder you choose – this PC, another drive or a NAS share. Starts a couple of minutes
  early and runs on a few minutes, reconnects if the stream drops, and warns when your plan's stream limit would be
  exceeded (each recording uses a stream, and so does watching). Dingus needs to be open (minimised is fine).
- **Search everything**: the search bar at the top works on every page (Ctrl+F) and finds channels, programmes in the
  TV guide, films and TV shows in one go. Filter the results to Channels, TV guide, Films or TV shows; Esc goes back to
  where you were. Right-click a future programme for a reminder.
- **Rearrange categories**: right-click a category in the Guide, Films or TV Shows to move it up, down, to the top or
  bottom (or back to the provider's order).
- **Fix the guide yourself**: right-click a channel → "Match TV guide…" to pick the right guide channel (with what's on
  now, to tell them apart), or "No guide". Kept across refreshes.
- **Info bar** (↑ / ↓ while watching): what's on other channels now and next, without leaving the one you're on.
- **Picture glitches?** The player's picture & sound menu has a per-channel fix: always deinterlace, the alternative
  graphics decoder, or software decoding.
- **Channel editor**: rename, hide and reorder channels (right-click a channel, or Settings → TV Guide). Kept across refreshes.
- **Picture & sound** (player button): aspect ratio, zoom / crop black bars, audio sync, subtitle timing, night mode.
  Preferred audio and subtitle languages in Settings → Playback.
- **Parental PIN**: lock categories (adult categories automatically). Locked categories are hidden everywhere until
  the PIN is entered with the lock button at the top.
- **Backup & restore** (Settings): everything in one .zip – passwords only if you choose to include them.
- **Themes**: Midnight, Slate (lighter grey) or Black, and six accent colours including teal.
- **Updates**: Dingus checks the GitHub releases once a day and offers new versions (always asks first).
- **One copy at a time**: opening Dingus again brings the running window to the front.

## Why it's light

- One shared VLC player with **hardware decoding** – the mini player and full screen are the same stream.
- Every long list (channels, categories, posters) is **virtualised**; only what's on screen exists.
- Logos and posters are cached on disk and decoded at display size off the UI thread.
- The guide is streamed (not loaded whole) and trimmed to the playlist's channels and a 3-day window.
- Opens instantly from cached data; refreshes in the background when out of date.

## Data location

`%LocalAppData%\Dingus` – settings, cached playlists and guides, images, reminders, the live TV buffer and `log.txt`.
(Settings from the old "Nimbus TV" name are moved here automatically.)
Passwords are encrypted with Windows DPAPI for your user account.
