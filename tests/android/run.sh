#!/usr/bin/env bash
# Runs inside the emulator step: installs the debug APK, loads the test playlist, walks the guide, menu, player,
# settings and updater with the remote's keys, and checks the log for what should have happened.
set -u
OUT=${OUT:-out}; mkdir -p "$OUT"
APK=$1
PKG=app.dingus.tv
fail=0
shot() { adb exec-out screencap -p > "$OUT/$1.png"; }
key() { adb shell input keyevent "$1"; sleep "${2:-1}"; }
wait_log() { # wait_log <pattern> <seconds>
  for _ in $(seq 1 "$2"); do adb logcat -d -s DINGUS-TEST:I | grep -qE "$1" && return 0; sleep 1; done; return 1; }
check() { if wait_log "$1" "$2"; then echo "PASS $3"; echo "PASS $3" >> "$OUT/result.txt"; else echo "::error title=android self-test::$3"; echo "FAIL $3" >> "$OUT/result.txt"; fail=1; fi; }
fresh() { adb logcat -c; }

adb install -r "$APK" || exit 1
fresh
adb logcat -s DINGUS-TEST:I AndroidRuntime:E > "$OUT/log-all.txt" &
adb shell am start -n $PKG/.MainActivity --es dingus_test_m3u http://10.0.2.2:8000/playlist.m3u
check "channels 3 categories 2" 60 "playlist loaded (3 channels, 2 categories)"
check "notice-done Playlist & TV guide updated" 60 "a note says when the playlist and guide have updated"
check "guide 3" 60 "guide read for all 3 channels"
sleep 3; shot 01-guide
if adb logcat -d -s DINGUS-TEST:I | grep -q "preview "; then
  echo "FAIL nothing plays until you choose a channel" >> "$OUT/result.txt"; fail=1
else echo "PASS nothing plays until you choose a channel" >> "$OUT/result.txt"; fi
key KEYCODE_DPAD_CENTER 1
check "preview Test 1 HD" 40 "OK plays the channel in the guide's window"
sleep 2; shot 01b-guide-playing

key KEYCODE_DPAD_RIGHT; key KEYCODE_DPAD_RIGHT; key KEYCODE_DPAD_DOWN 2; shot 02-guide-later
fresh
key KEYCODE_DPAD_CENTER 2; shot 02b-reminder-dialog
key KEYCODE_DPAD_CENTER 2
check "reminder-set true" 5 "OK on a later programme sets a reminder"
shot 02c-reminder-set
fresh
key KEYCODE_DPAD_LEFT; key KEYCODE_DPAD_LEFT; key KEYCODE_DPAD_LEFT 2
check "categories-open" 5 "LEFT from what's on now shows the categories"
shot 03-categories
# Scrolling slowly through the categories (each one shows behind) mustn't jump back to "All channels".
fresh
key KEYCODE_DPAD_DOWN 1.5; key KEYCODE_DPAD_DOWN 1.5; key KEYCODE_DPAD_CENTER 3   # Sport
check "key KEYCODE_DPAD_CENTER on TextView/Sport" 5 "scrolling through categories keeps the highlight where you are"
key KEYCODE_DPAD_CENTER 3; shot 03b-sport
check "preview Test 3 Sport" 30 "picking a category shows its channels"
fresh
key KEYCODE_DPAD_LEFT 2; key KEYCODE_DPAD_UP 1.5; key KEYCODE_DPAD_CENTER 4        # back to UK: Entertainment
check "key KEYCODE_DPAD_CENTER on TextView/UK: Entertainment" 5 "moving up a category works too"
key KEYCODE_DPAD_CENTER 1
check "preview Test 1 HD" 30 "the guide shows that category's channels"
fresh
key KEYCODE_DPAD_DOWN 2
if adb logcat -d -s DINGUS-TEST:I | grep -q "preview Test 2"; then
  echo "FAIL moving down doesn't start the next channel by itself" >> "$OUT/result.txt"; fail=1
else echo "PASS moving down doesn't start the next channel by itself" >> "$OUT/result.txt"; fi
key KEYCODE_DPAD_CENTER 1
check "preview Test 2" 40 "OK plays it in the window (HLS)"
sleep 2; shot 04-category
fresh
key KEYCODE_DPAD_CENTER 2
check "fullscreen Test 2" 15 "OK again on the same channel grows it to full screen (same stream)"
sleep 3
if adb logcat -d -s DINGUS-TEST:I | grep -q "playing Test 2"; then
  echo "::error title=android self-test::the stream loaded again going full screen"; echo "FAIL full screen keeps the preview's stream (no second load)" >> "$OUT/result.txt"; fail=1
else echo "PASS full screen keeps the preview's stream (no second load)" >> "$OUT/result.txt"; fi
shot 04b-fullscreen
fresh
key KEYCODE_DPAD_UP
check "playing Test 1 HD" 45 "changing channel in full screen"
sleep 1; shot 05-player-info
sleep 15
fresh
key KEYCODE_DPAD_LEFT 0.3; key KEYCODE_DPAD_LEFT 2
check "timeshift behind (1[0-9]|2[0-9])s" 10 "LEFT rewinds live TV"
check "playing Test 1 HD" 20 "playback carries on from the earlier point"
sleep 2; shot 05b-rewound
key KEYCODE_MEDIA_PLAY_PAUSE 2
check "timeshift paused" 5 "play/pause pauses live TV"
shot 05c-paused
key KEYCODE_MEDIA_PLAY_PAUSE 1
key KEYCODE_MENU 2; shot 05d-options
key KEYCODE_DPAD_CENTER 2   # Add to Favourites
fresh
key KEYCODE_BACK; key KEYCODE_BACK 2
check "guide-shown Test 1 HD" 10 "BACK shrinks back to the guide, on the channel just watched"
sleep 2; shot 06-guide-after

fresh
key KEYCODE_BACK 1; key KEYCODE_DPAD_LEFT 2
check "menu-open" 5 "LEFT from the categories opens the main menu"
shot 06b-menu
for _ in 1 2 3 4 5 6 7; do key KEYCODE_DPAD_DOWN 0.5; done
key KEYCODE_DPAD_UP 0.5                                                               # (Refresh playlist is at the bottom)
key KEYCODE_DPAD_CENTER 2
check "settings-shown" 10 "Settings opens from the menu"
shot 07-settings
for _ in $(seq 1 9); do key KEYCODE_DPAD_DOWN 0.4; done
sleep 1; shot 07b-settings-more
for _ in $(seq 1 8); do key KEYCODE_DPAD_DOWN 0.4; done
sleep 1; shot 07c-settings-end
key KEYCODE_BACK 2

fresh
adb shell am start -n $PKG/.MainActivity --es dingus_test_update_api http://10.0.2.2:8000/release.json
check "update-found 99.0.0" 30 "finds an update on the release page"
check "update-downloaded [0-9]+" 60 "downloads the update"
check "update-installer-opened|update-needs-permission" 15 "hands the update to Android's installer"
sleep 2; shot 08-update
key KEYCODE_BACK 2

# ---- An Xtream Codes login: films and TV shows.
fresh
adb shell am start -n $PKG/.MainActivity --es dingus_test_xtream http://10.0.2.2:8000
check "channels 3 categories 2" 60 "Xtream login: live channels loaded"
check "guide 3" 60 "Xtream login: guide read"
sleep 3
fresh
key KEYCODE_DPAD_LEFT 2
shot 08b-catchup-guide
key KEYCODE_DPAD_CENTER 3
check "catchup Test 1 HD" 10 "LEFT into earlier programmes and OK starts catch-up"
check "vod-playing" 40 "the catch-up programme plays"
sleep 2; shot 08c-catchup-playing
key KEYCODE_BACK 3
fresh
key KEYCODE_BACK 1
check "categories-open" 5 "BACK shows the categories"
key KEYCODE_DPAD_LEFT 2
check "menu-open" 5 "then the main menu"
key KEYCODE_DPAD_DOWN 0.5                                # Films (under Live TV)
shot 09-menu-xtream
key KEYCODE_DPAD_CENTER 3
check "library movies 3" 40 "Films load"
sleep 3; shot 10-films
fresh
key KEYCODE_DPAD_RIGHT 1; key KEYCODE_DPAD_CENTER 3
check "details .* episodes 0" 30 "a film's details load"
sleep 2; shot 11-film-details
fresh
key KEYCODE_DPAD_CENTER 3
check "vod-playing" 40 "the film plays"
for _ in 1 2 3 4 5; do key KEYCODE_DPAD_RIGHT 0.5; done
sleep 2; shot 12-film-playing
fresh
key KEYCODE_BACK 3
check "detail-button .*Resume from" 10 "coming back offers to resume"
shot 13-film-resume
key KEYCODE_BACK 2; key KEYCODE_BACK 2
fresh
key KEYCODE_BACK 1; key KEYCODE_DPAD_LEFT 2
for _ in 1 2; do key KEYCODE_DPAD_DOWN 0.5; done; key KEYCODE_DPAD_CENTER 3    # menu → TV Shows
check "library series 2" 40 "TV shows load"
sleep 2
fresh
key KEYCODE_DPAD_RIGHT 1; key KEYCODE_DPAD_CENTER 3
check "details .* episodes 4" 30 "a show's seasons and episodes load"
sleep 3; shot 14-show-details
fresh
adb shell am start -n $PKG/.MainActivity --es dingus_test_search test
check "search \"test\" [1-9]" 30 "search finds channels, films and shows"
sleep 2; shot 15-search

# ---- Home
fresh
adb shell am start -n $PKG/.HomeActivity
check "home rows [1-9]" 30 "Home shows its rows"
sleep 4; shot 16-home
key KEYCODE_DPAD_DOWN 3; shot 17-home-row2
fresh
key KEYCODE_DPAD_UP 1
check "home-focus row 0 heroWatch" 5 "UP from the rows goes back up to the top"
key KEYCODE_DPAD_LEFT 1.5
check "menu-open" 5 "LEFT at the left edge of Home opens the side menu"
shot 17b-home-menu
key KEYCODE_DPAD_DOWN 0.5; key KEYCODE_DPAD_DOWN 0.5; key KEYCODE_DPAD_CENTER 3     # Home → Live TV → Films
check "library movies 3" 30 "Films opens from the side menu"
sleep 3; shot 18-films-rows
key KEYCODE_DPAD_DOWN 3; shot 19-films-rows-2
fresh
key KEYCODE_DPAD_LEFT 1.5
check "menu-open" 5 "the side menu opens on Films too"
shot 19a-films-menu
key KEYCODE_DPAD_RIGHT 1
check "menu-closed" 5 "RIGHT closes it"
fresh
key KEYCODE_BACK 2
check "home-shown" 10 "BACK from Films returns to Home"
key KEYCODE_DPAD_UP 0.5; key KEYCODE_DPAD_UP 0.5; key KEYCODE_DPAD_LEFT 1.5
for _ in 1 2 3 4 5; do key KEYCODE_DPAD_DOWN 0.4; done                                 # … → Settings
key KEYCODE_DPAD_CENTER 2
check "settings-shown" 10 "Settings opens from the side menu on Home"
key KEYCODE_BACK 1
# Live TV from a freshly opened Home (the channels are already loaded by Home)
fresh
adb shell am force-stop $PKG
adb shell am start -n $PKG/.HomeActivity
check "home rows [1-9]" 30 "Home opens by itself"
sleep 3
key KEYCODE_DPAD_LEFT 1.5; key KEYCODE_DPAD_DOWN 0.5; key KEYCODE_DPAD_CENTER 3     # menu → Live TV
check "guide-filled [1-9]" 15 "Live TV from Home shows the channels"
shot 19aa-live-from-home

# ---- Channel numbers off
fresh
adb shell am start -n $PKG/.MainActivity --ez dingus_test_numbers_off true
check "numbers false" 10 "channel numbers can be turned off"
sleep 3; shot 19b-guide-no-numbers

# ---- Built-in VPN (a real WireGuard server runs on the test machine)
if [ -n "${WG_CONF:-}" ] && [ -f "$WG_CONF" ]; then
  adb shell appops set $PKG ACTIVATE_VPN allow
  fresh
  adb shell am start -n $PKG/.MainActivity --ez dingus_test_vpn_phone true
  check "phone-setup code" 15 "VPN: the phone setup page starts"
  line=$(adb logcat -d -s DINGUS-TEST:I | grep -o "phone-setup code [0-9]* port [0-9]*" | tail -1)
  CODE=$(echo "$line" | awk '{print $3}'); PORT=$(echo "$line" | awk '{print $5}')
  sleep 1; shot 20-vpn-phone
  adb forward tcp:18765 tcp:$PORT
  curl -s -m 10 http://127.0.0.1:18765/ > "$OUT/phone-page.html"
  curl -s -m 10 -X POST http://127.0.0.1:18765/save --data-urlencode "code=$CODE" --data-urlencode "name=Test" --data-urlencode "conf@$WG_CONF" > "$OUT/phone-reply.html"
  check "vpn-imported Test" 10 "VPN: a config file sent from a phone is saved"
  sleep 1; key KEYCODE_BACK 2; shot 21-vpn-settings
  fresh
  adb shell am start -n $PKG/.MainActivity --es dingus_test_vpn_connect Test
  check "vpn on Test" 30 "VPN: connects"
  fresh
  adb shell am start -n $PKG/.MainActivity --es dingus_test_m3u http://10.66.0.1:8000/playlist.m3u
  check "channels 3 categories 2" 60 "VPN: the playlist loads through the tunnel"
  sleep 4; shot 22-guide-vpn
  fresh
  adb shell am start -n $PKG/.MainActivity --ez dingus_test_vpn_off true
  check "vpn off" 15 "VPN: disconnects"
fi

adb shell dumpsys meminfo $PKG | grep -E "TOTAL( PSS)?:" | head -1 | sed 's/^/memory: /' | tee -a "$OUT/result.txt"
adb logcat -d -s DINGUS-TEST:I AndroidRuntime:E > "$OUT/log.txt"
adb logcat -d -v brief '*:W' | grep -v "com.android.tv\|AsyncDvr" | tail -1500 > "$OUT/log-warnings.txt"
kill %1 2>/dev/null
if grep -A1 "FATAL EXCEPTION" "$OUT/log-all.txt" | grep -q "Process: $PKG"; then echo "::error title=android self-test::app crashed"; fail=1; fi
[ $fail = 0 ] && echo PASSED >> "$OUT/result.txt" || echo FAILED >> "$OUT/result.txt"
exit $fail
