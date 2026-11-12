#!/usr/bin/env bash
# Runs inside the emulator step: installs the debug APK, loads the test playlist, walks the screens with the
# remote's keys and checks the log for what should have happened. Screenshots go to $OUT.
set -u
OUT=${OUT:-out}; mkdir -p "$OUT"
APK=$1
PKG=app.dingus.tv
fail=0
shot() { adb exec-out screencap -p > "$OUT/$1.png"; }
key() { adb shell input keyevent "$1"; sleep "${2:-1}"; }
wait_log() { # wait_log <pattern> <seconds>
  for _ in $(seq 1 "$2"); do adb logcat -d -s DINGUS-TEST:I | grep -q "$1" && return 0; sleep 1; done; return 1; }
check() { if wait_log "$1" "$2"; then echo "PASS $3"; echo "PASS $3" >> "$OUT/result.txt"; else echo "::error title=android self-test::$3"; echo "FAIL $3" >> "$OUT/result.txt"; fail=1; fi; }

adb install -r "$APK" || exit 1
adb logcat -c
adb logcat -s DINGUS-TEST:I AndroidRuntime:E > "$OUT/log-all.txt" &
adb shell am start -n $PKG/.MainActivity --es dingus_test_m3u http://10.0.2.2:8000/playlist.m3u
check "channels 3 categories 2" 60 "playlist loaded (3 channels, 2 categories)"
check "guide 3" 60 "guide read for all 3 channels"
sleep 3; shot 01-channels
key KEYCODE_DPAD_LEFT; key KEYCODE_DPAD_DOWN 2; shot 02-category
key KEYCODE_DPAD_RIGHT; key KEYCODE_DPAD_UP; key KEYCODE_DPAD_UP
key KEYCODE_DPAD_CENTER 2
check "playing Test 1 HD" 45 "Test 1 plays (MPEG-TS)"
sleep 2; shot 03-player-info
sleep 5; shot 04-player
key KEYCODE_DPAD_DOWN
check "playing Test 2" 45 "Test 2 plays (HLS) after changing channel"
sleep 1; shot 05-zap
key KEYCODE_MENU 2; shot 06-favourite
adb logcat -c
key KEYCODE_BACK; key KEYCODE_BACK 2
check "main-shown" 10 "BACK returns to the channel list"
key KEYCODE_DPAD_LEFT; key KEYCODE_DPAD_UP; key KEYCODE_DPAD_UP; key KEYCODE_DPAD_UP 2
shot 07-back-favourites
adb shell dumpsys meminfo $PKG | grep -E "TOTAL( PSS)?:" | head -1 | sed 's/^/memory: /' | tee -a "$OUT/result.txt"
adb logcat -d -s DINGUS-TEST:I AndroidRuntime:E > "$OUT/log.txt"
kill %1 2>/dev/null; if grep -A1 "FATAL EXCEPTION" "$OUT/log-all.txt" | grep -q "Process: $PKG"; then echo "::error title=android self-test::app crashed"; fail=1; fi
[ $fail = 0 ] && echo PASSED >> "$OUT/result.txt" || echo FAILED >> "$OUT/result.txt"
exit $fail
