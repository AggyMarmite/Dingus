# Dingus

A lightweight, modern IPTV player for Windows 10/11 (WPF + .NET 8, VLC video engine).

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

### Making a release

Push a version tag. GitHub Actions builds the app, makes the installer and attaches it to a new release:

```powershell
git tag v0.3.0
git push origin v0.3.0
```

## What's in this build

- **TV Guide** – Sky/TiviMate-style grid with channel logos, wrapped channel names, a live mini player,
  now/next details, a red "now" line, earlier/later/now navigation, favourites (right-click a channel)
  and a category rail. Search covers every channel, not just the selected category.
- **EPG auto-matching** – guide channels are matched by EPG id first, then by normalised name
  ("UK: BBC One HD" ↔ "BBC One"), so most channels line up without manual work. Missing logos fall back to the guide's icons.
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
| Ctrl + F | Search everything |
| ? / F1 | Keyboard shortcut list |
| Home | Start a film or episode from the beginning |
| N | Next episode |
| M | Mute · mouse wheel = volume |
| Media keys | Play/pause, stop, next/previous work even when the app isn't focused (while something plays) |

### Watching

- **Home**: carry on watching (live preview of your last channel), account status, coming up next on your favourites,
  live sport now, tonight's picks (films and sport), continue watching, recently watched, favourites, new films and new shows.
  (Settings → TV Guide → "Open on the TV Guide" to start on the guide instead.)
- Opens on the channel you last watched; **Recently watched** is in the guide's categories.
- **Continue watching** in Films and TV Shows; films and episodes resume where you stopped.
  At the end of an episode, the next one starts after a 10-second countdown.
- **Catch-up** (Xtream providers that offer it): a ⟲ mark on guide programmes – double-click one,
  or use "Start over" / "Watch catch-up" on the programme details.
- **Stream health**: if a stream stalls or errors, it reconnects automatically (up to 3 tries).
- **Merged channels**: "BBC One", "BBC One HD", "BBC One FHD" and backups show as one channel; if a version
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
- **Search everything** (Search in the left rail, or Ctrl+F): channels, programmes in the TV guide, films and TV shows
  in one go. Right-click a future programme for a reminder.
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
