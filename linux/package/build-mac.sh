#!/usr/bin/env bash
# Builds Dingus-<version>-mac-<arch>.dmg: Dingus.app (self-contained .NET) with its own copy of the VLC video engine,
# so it runs without installing anything else. Run on a Mac (GitHub's macOS runners).
#   linux/package/build-mac.sh <version> <arm64|x64> <output folder>
set -Eeuo pipefail
report() { echo "::error title=Mac build::line $1: $2"; }
trap 'report $LINENO "$BASH_COMMAND"' ERR
VERSION="${1:-0.0.0}"
ARCH="${2:-arm64}"
OUT="$(mkdir -p "${3:-out}" && cd "${3:-out}" && pwd)"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="$(mktemp -d)"
VLC_VERSION=3.0.21
case "$ARCH" in arm64) LIPO=arm64 ;; x64) LIPO=x86_64 ;; *) echo "arch must be arm64 or x64"; exit 1 ;; esac

APP="$WORK/stage/Dingus.app"
C="$APP/Contents"
mkdir -p "$C/MacOS" "$C/Resources" "$C/Frameworks/vlc"

echo "== Publishing Dingus $VERSION for macOS ($ARCH)"
dotnet publish "$ROOT/linux/Dingus.Linux.csproj" -c Release -r "osx-$ARCH" --self-contained true \
  -p:Version="$VERSION" -p:DebugType=none -p:UseAppHost=true -o "$C/MacOS"
rm -f "$C/MacOS/createdump" "$C/MacOS/libmscordbi.dylib" "$C/MacOS/libmscordaccore.dylib"
chmod +x "$C/MacOS/dingus"

echo "== Copying the VLC video engine (VLC $VLC_VERSION)"
DMG="$WORK/vlc.dmg"
curl -fsSL --retry 3 -o "$DMG" "https://download.videolan.org/pub/videolan/vlc/$VLC_VERSION/macosx/vlc-$VLC_VERSION-universal.dmg"
MNT="$WORK/vlc-mount"
mkdir -p "$MNT"
hdiutil attach -nobrowse -readonly -noautoopen -mountpoint "$MNT" "$DMG" > /dev/null
VLCAPP="$(find "$MNT" -maxdepth 1 -name '*.app' | head -1)"
ditto "$VLCAPP/Contents/MacOS/lib" "$C/Frameworks/vlc/lib"
ditto "$VLCAPP/Contents/MacOS/plugins" "$C/Frameworks/vlc/plugins"
hdiutil detach -quiet "$MNT" || hdiutil detach -quiet -force "$MNT" || true
V="$C/Frameworks/vlc"
# libvlc.dylib / libvlccore.dylib are the names LibVLCSharp looks for.
( cd "$V/lib"
  for n in libvlc libvlccore; do
    if [ ! -e "$n.dylib" ]; then f=$(ls $n.*.dylib | head -1); ln -s "$f" "$n.dylib"; fi
  done
  ls -la )
# Leave out what Dingus never uses: VLC's own window and interfaces, streaming out, visualisations, Lua scripts, discs.
P="$V/plugins"
find "$P" \( -name 'libmacosx_plugin*' -o -name 'libminimal_macosx*' -o -name 'libqt*' -o -name 'libskins*' -o -name 'liblua*' \
  -o -name 'libstream_out_*' -o -name 'libmux_*' -o -name 'libaccess_output_*' -o -name 'libgoom*' -o -name 'libvisual*' \
  -o -name 'libprojectm*' -o -name 'libglspectrum*' -o -name '*dvd*' -o -name '*bluray*' -o -name '*cdda*' -o -name '*vcd*' \
  -o -name 'libcaca*' -o -name 'libncurses*' -o -name 'libhotkeys*' -o -name 'libgestures*' -o -name 'libnetsync*' \
  -o -name 'libavcapture*' -o -name 'libqtsound*' \) -delete
# Only this Mac's half of each universal library (halves the size).
find "$V" -type f -name '*.dylib' | while read -r f; do
  if lipo -archs "$f" 2>/dev/null | grep -q ' '; then lipo -thin "$LIPO" "$f" -output "$f.thin" && mv "$f.thin" "$f"; fi
done
rm -f "$P/plugins.dat" # built for the full plugin set – VLC rebuilds it as needed

echo "== Icon and app details"
ICONSET="$WORK/Dingus.iconset"
mkdir -p "$ICONSET"
SRC="$ROOT/linux/Assets/Dingus-256.png"
for s in 16 32 64 128 256 512; do
  sips -z $s $s "$SRC" --out "$ICONSET/icon_${s}x${s}.png" > /dev/null
  d=$((s * 2)); [ $d -le 1024 ] && sips -z $d $d "$SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" > /dev/null
done
rm -f "$ICONSET/icon_64x64.png" "$ICONSET/icon_64x64@2x.png"
iconutil -c icns "$ICONSET" -o "$C/Resources/Dingus.icns"
cat > "$C/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Dingus</string>
    <key>CFBundleDisplayName</key><string>Dingus</string>
    <key>CFBundleIdentifier</key><string>app.dingus</string>
    <key>CFBundleExecutable</key><string>dingus</string>
    <key>CFBundleIconFile</key><string>Dingus</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.entertainment</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
    <key>NSLocalNetworkUsageDescription</key><string>Dingus plays TV from your IPTV provider, which can be on your home network.</string>
</dict>
</plist>
PLIST

echo "== Signing (ad hoc)"
# Apple Silicon only runs signed code. Without a paid Apple Developer ID this is an "ad hoc" signature,
# so the first time it's opened macOS asks the user to confirm (System Settings → Privacy & Security → Open Anyway).
BAD=0
while read -r f; do
  [ "$f" = "$C/MacOS/dingus" ] && continue # signed with the app below
  # Everything in Contents/MacOS counts as code to macOS (the .NET .dll files too); elsewhere only real binaries.
  if [[ "$f" == "$C/MacOS/"* ]] || file -b "$f" | grep -q 'Mach-O'; then
    if ! msg=$(codesign --force --sign - --timestamp=none "$f" 2>&1); then echo "codesign failed: ${f#$C/}: $msg"; BAD=1; fi
  fi
done < <(find "$C" -type f)
[ $BAD = 0 ] || exit 1
codesign --force --sign - --timestamp=none "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

echo "== Making the disk image"
ln -s /Applications "$WORK/stage/Applications"
NAME="Dingus-$VERSION-mac-$ARCH.dmg"
rm -f "$OUT/$NAME"
hdiutil create -quiet -volname "Dingus" -srcfolder "$WORK/stage" -fs HFS+ -format ULFO -ov "$OUT/$NAME"
# Keep the app itself for the self-test.
rm -rf "$OUT/Dingus-$ARCH.app"
ditto "$APP" "$OUT/Dingus-$ARCH.app"
du -sh "$APP" "$OUT/$NAME"
