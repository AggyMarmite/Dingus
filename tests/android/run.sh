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
check "guide 3" 60 "guide read for all 3 channels"
check "preview Test 1 HD" 40 "the highlighted channel plays in the preview window"
sleep 2; shot 01-guide

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
key KEYCODE_DPAD_DOWN; key KEYCODE_DPAD_CENTER 4   # UK: Entertainment
check "preview Test 1 HD" 20 "picking a category shows its channels"
fresh
key KEYCODE_DPAD_DOWN
check "preview Test 2" 40 "moving down previews the next channel (HLS)"
sleep 2; shot 04-category
fresh
key KEYCODE_DPAD_CENTER 2
check "playing Test 2" 45 "OK plays the channel full screen"
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
check "main-shown" 10 "BACK returns to the guide"
check "preview Test 1 HD" 30 "the guide lands on the channel just watched"
sleep 2; shot 06-guide-after

fresh
key KEYCODE_BACK 1; key KEYCODE_DPAD_LEFT 2
check "menu-open" 5 "LEFT from the categories opens the main menu"
shot 06b-menu
for _ in 1 2 3 4 5 6 7; do key KEYCODE_DPAD_DOWN 0.5; done
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
key KEYCODE_DPAD_DOWN 0.5; key KEYCODE_DPAD_DOWN 0.5   # Search, then Films
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
for _ in 1 2 3; do key KEYCODE_DPAD_DOWN 0.5; done; key KEYCODE_DPAD_CENTER 3    # menu → TV Shows
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
key KEYCODE_DPAD_UP 1; key KEYCODE_DPAD_UP 1; key KEYCODE_DPAD_UP 1
key KEYCODE_DPAD_RIGHT 0.5; key KEYCODE_DPAD_RIGHT 0.5; key KEYCODE_DPAD_CENTER 3   # tab: Films
check "library movies 3" 30 "Films opens from Home"
sleep 3; shot 18-films-rows
key KEYCODE_DPAD_DOWN 3; shot 19-films-rows-2

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
