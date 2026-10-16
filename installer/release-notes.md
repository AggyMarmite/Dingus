## What's new in Dingus 0.5.0

* **Fix the guide yourself** – right-click a channel with the wrong (or no) guide → **Match TV guide…**, search, and pick
  the right one; each guide channel shows what's on now so you can tell them apart. Or choose "No guide". Your choices
  are kept when the playlist refreshes.
* **Sky-style info bar** – while watching, ↑ / ↓ show what's on other channels now and next, without changing.
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

**Install:** download `Dingus-Setup-0.5.0.exe` below – or, if you already have Dingus, it will offer this update itself.
