## What's new in Dingus 0.10.0

* New Events section: find a game and follow your teams.
* Reminders for your teams' games.
* Bug fixes and other improvements.

## What's new in Dingus 0.9.9

* Watch trailers for films and shows.
* Volume evened out between channels.
* My List on Linux.
* Bug fixes and other improvements.

## What's new in Dingus 0.9.8

* Linux version brought up to date with Windows.
* Bug fixes and other improvements.

## What's new in Dingus 0.9.7

* New splash screen when Dingus opens.
* Faster start-up and playlist loading.
* Smoother scrolling and animations.
* Bug fixes and other improvements.

## What's new in Dingus 0.9.6

* **What's on, in the mini player** – hover over picture-in-picture to see the channel, the programme and how long is
  left, with a thin progress line (for films and episodes: the time left).
* **Time left in the player** – the full player now shows how long is left of the programme, film or episode.
* **No more "always on top" after full screen** – leaving full screen could leave Dingus stuck above every other app.
  Now only full screen (while Dingus is in front) and picture-in-picture stay on top – and Dingus double-checks with
  Windows itself, including the player's controls over the video, whenever you leave full screen or switch apps.

## What's new in Dingus 0.9.5

* **Volume in the mini player** – picture-in-picture has a mute button in its bottom-left corner; hover over it and a
  volume slider slides out. It's the same volume as the full player.

## What's new in Dingus 0.9.4

* **Opens as a window** – Dingus no longer fills the whole screen when it starts: it opens centred at about 80% of the
  screen (or the size you last left it, if that's smaller). Maximise it with the button or by double-clicking the
  title bar whenever you like.

## What's new in Dingus 0.9.3

* **The logo goes Home again** – clicking the Dingus logo at the top left now opens Home (in 0.9.1 and 0.9.2 Windows
  treated the click as the start of moving the window).

## What's new in Dingus 0.9.2

* **Recordings show the right length** – live streams sometimes jump in time (the provider restarts the stream, or
  Dingus reconnects after a drop), and players then worked out the recording's length from those jumps – so a
  30-minute episode could show as 6 hours, and seeking went wrong. Dingus now keeps the recording's timeline running
  straight through. Recordings you already have are checked once in the background and repaired if needed (the
  pictures and sound aren't changed).

## What's new in Dingus 0.9.1

* **New logo** – Dingus has a new icon: a white "D" with a play button, on purple. You'll see it on the
  taskbar, Start menu, desktop shortcut, installer and app window – and at the top left of Dingus itself.
* **The Dingus logo is the Home button** – click the logo at the top left to go Home (the separate Home tab has gone,
  leaving more room for the others).
* **Cleaner channel logos** – the channel's initials (shown when there's no logo) no longer peek through logos with a
  see-through background once the logo has loaded.

## What's new in Dingus 0.9.0

* **My List** – save films and shows for later: the **My List** button on a film or show's page, or right-click its
  poster (in Films, TV Shows or on Home). They appear in a My List row on Home and under **My List** in Films and TV Shows,
  with a tick on the poster. Each playlist keeps its own list.
* **Faster with big playlists** – tested with 30,000 channels, 80,000 films, 15,000 shows and a 300,000-programme
  guide. Reading the playlist and building the Live TV channel list now happen in the background, Home no longer sorts
  every film to find the newest, and memory is tidied up once you leave the window alone rather than while you're
  using it – so the window keeps responding while a big playlist loads (the longest freezes are a third of what they were).
  Dingus also starts and opens its first pages a little quicker.
* **Smoother Home page** – the featured picture now cross-fades straight into the next one (no more dip to black), with a
  gentle settling zoom; the words drift in as the picture changes, and the slide dots glide along. Dingus waits for the
  next picture to load before changing, and draws the blurred poster background once instead of on every frame.

## What's new in Dingus 0.8.0

* **Dingus for Linux** – the same app, now on Linux as an AppImage (`Dingus-0.8.0-x86_64.AppImage`): Home, Live TV with
  the guide and preview, the full-screen player with pause & rewind and catch-up, picture-in-picture, Multi-view, Films,
  TV Shows, Search, Recordings & reminders, Settings, parental PIN, backup & restore and in-app updates. It carries its own
  copy of VLC, so there's nothing else to install – make the file executable and open it.

## What's new in Dingus 0.7.1

* **New Home page** – a big featured picture across the top with the title, details and **Watch** / **More info**
  buttons. It moves through your last channel, films and episodes you're part-way through, what's on your favourites,
  and new films and shows (dots and the arrow to move on yourself; it pauses while the mouse is over it). Below it, rows
  of channels and titles – favourite channels now sit on tiles in their logo's colour, with "View all" links.
* **Tidier tabs** – a little more space between the tabs along the top, so the hover highlights don't touch.
* **Programme artwork on Home** – the featured picture shows the programme itself rather than just the channel logo. Dingus uses the TV guide's own image when it has
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
