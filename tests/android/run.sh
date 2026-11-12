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
key KEYCODE_DPAD_LEFT; key KEYCODE_DPAD_LEFT; key KEYCODE_DPAD_LEFT 2
check "menu-open" 5 "LEFT from what's on now opens the categories menu"
shot 03-menu
key KEYCODE_DPAD_DOWN 2; key KEYCODE_DPAD_CENTER 2   # UK: Entertainment
fresh
key KEYCODE_DPAD_DOWN
check "preview Test 2" 40 "moving down previews the next channel (HLS)"
sleep 2; shot 04-category
fresh
key KEYCODE_DPAD_CENTER 2
check "playing Test 2" 45 "OK plays the channel full screen"
key KEYCODE_DPAD_UP
check "playing Test 1 HD" 45 "changing channel in full screen"
sleep 1; shot 05-player-info
key KEYCODE_MENU 2
fresh
key KEYCODE_BACK; key KEYCODE_BACK 2
check "main-shown" 10 "BACK returns to the guide"
check "preview Test 1 HD" 30 "the guide lands on the channel just watched"
sleep 2; shot 06-guide-after

fresh
key KEYCODE_BACK 2
for _ in 1 2 3 4 5 6 7; do key KEYCODE_DPAD_DOWN 0.5; done
key KEYCODE_DPAD_CENTER 2
check "settings-shown" 10 "Settings opens from the menu"
shot 07-settings

fresh
adb shell am start -n $PKG/.MainActivity --es dingus_test_update_api http://10.0.2.2:8000/release.json
check "update-found 99.0.0" 30 "finds an update on the release page"
check "update-downloaded [0-9]+" 60 "downloads the update"
check "update-installer-opened|update-needs-permission" 15 "hands the update to Android's installer"
sleep 2; shot 08-update

adb shell dumpsys meminfo $PKG | grep -E "TOTAL( PSS)?:" | head -1 | sed 's/^/memory: /' | tee -a "$OUT/result.txt"
adb logcat -d -s DINGUS-TEST:I AndroidRuntime:E > "$OUT/log.txt"
kill %1 2>/dev/null
if grep -A1 "FATAL EXCEPTION" "$OUT/log-all.txt" | grep -q "Process: $PKG"; then echo "::error title=android self-test::app crashed"; fail=1; fi
[ $fail = 0 ] && echo PASSED >> "$OUT/result.txt" || echo FAILED >> "$OUT/result.txt"
exit $fail
