## What's new in Dingus 0.7.1

* **Programme artwork on Home** – "Carry on watching" now shows a picture of the programme itself (e.g. a sitcom's own artwork)
  instead of just the channel logo, with the logo as a small badge. Dingus uses the TV guide's own image when it has
  one, then a matching show or film in your playlist, then TVmaze (a free TV database). Only the programme's name is
  sent, and each title is looked up once. You can turn the online lookup off in Settings → Programme artwork.

## What's new in Dingus 0.7.0

* **New title bar** – the side rail is gone: Home · Live TV · Films · TV Shows · Recordings, the search bar, playlist,
  refresh and settings now sit in one bar along the top (with Dingus's own minimise / maximise / close). More room for
  everything else. On narrower windows the tab labels tuck away to icons.
* **"Guide" is now "Live TV".**
* **Search from anywhere** – the search bar works on every page and finds channels, programmes, films and TV shows,
  with filters (All · Channels · TV guide · Films · TV shows) and "See all". Esc goes back to where you were.
* **Recordings & reminders** – a new Recordings tab. Record from the right-click menu (Live TV, Search, Home), the
  Record button on a programme, or **R** / the record button in the player; or "Record a time slot…". Choose where
  recordings are saved – this PC, another drive or a NAS share – with early start / overrun padding. It warns when your
  plan's stream limit would be exceeded (each recording uses a stream, and so does watching). Dingus needs to be open
  (minimised is fine) for recordings to happen.
* **Home opens instantly** – "Carry on watching" shows the channel logo with a Play button instead of starting the
  stream straight away.
* **Fixed:** full screen and Multi-view no longer stay on top of other apps after you click away.

## Dingus 0.6.1

* **New picture-in-picture icon** – a screen with a small picture in the corner, in the player and on the previews.
* **Full-screen button on the Guide and Home preview** – next to pause and picture-in-picture (double-click still works too).

## Dingus 0.6.0

* **Search everything** – a new **Search** page (left rail, or **Ctrl+F** from anywhere) finds channels, programmes
  in the TV guide, films and TV shows at once. Click a programme that's on to watch it, or right-click a future one
  for a reminder.
* **Rearrange categories** – right-click any category in the Guide, Films or TV Shows → Move up / down / to the top /
  to the bottom. Kept when the playlist refreshes.
* **Cleaner lists** – channel numbers and the channel counts next to categories are gone (typing a number still tunes).
* **Smooth button feedback** – buttons, chips, menus, the left rail, list rows and switches now fade their highlights,
  buttons give a tiny press, posters lift slightly on hover and switches slide.
* **Screen stays on while you watch** – Windows won't dim the screen or go to sleep while something's playing.
* **Fixed:** sub-menus in the player's picture & sound menu (aspect ratio, crop, audio sync, picture fixes) now open.

## Dingus 0.5.0

* **Fix the guide yourself** – right-click a channel with the wrong (or no) guide → **Match TV guide…**, search, and pick
  the right one; each guide channel shows what's on now so you can tell them apart. Or choose "No guide". Your choices
  are kept when the playlist refreshes.
* **Now & next info bar** – while watching, ↑ / ↓ show what's on other channels now and next, without changing.
  **Enter** watches, **Esc** closes. Page Up / Page Down still change channel straight away.
* **Smoother full screen** – no more flicker/animation when double-clicking into or out of full screen.
* **Smoother everywhere** – controls, toasts, the channel list, the info bar and pages now fade and slide in
  (60 fps, only while moving – no extra load at rest).
* **Picture glitches on a channel?** Picture & sound menu → *Picture glitches on this channel?* – always remove
  interlacing lines (comb-like lines on movement), use the alternative graphics decoder (blocky or green glitches),
  or software decoding. Remembered per channel.

## Dingus 0.4.2

* **Settings work while a playlist loads** – the "Loading playlist" screen no longer blocks the app. Open Settings
  (from the left, or the new "Open Settings" button on the loading screen) to fix a login, switch playlist or change
  anything else; the loading carries on in the corner. Go back to any other page and the loading screen returns
  until the playlist is ready.

## Dingus 0.4.1

* Updating from 0.3.x now clears out the old program files first, so the smaller version starts cleanly.

## Dingus 0.4.0

* **Catch-up on the live timeline** – on channels with catch-up, drag the bar under the live picture to go back
  through the programme (and earlier), then carry on watching or drag right to return to live.
  ← / → move 10 seconds (Ctrl + ← / → five minutes), **Home** goes to the start of the programme, **End** goes back to live.
  Dingus uses its own rewind buffer when it can and the provider's catch-up for anything further back.
  "Start over" and "Watch catch-up" in the guide now open on this timeline too.
* **Much smaller download** – the installer is about 19 MB (was 98 MB), and updates are just as small. Unused parts of
  the video engine have been left out. If your PC doesn't have Microsoft's .NET 8 Desktop Runtime yet, the installer
  fetches it once (most PCs already have it).

**Install:** download `Dingus-Setup-0.7.0.exe` below – or, if you already have Dingus, it will offer this update itself.
