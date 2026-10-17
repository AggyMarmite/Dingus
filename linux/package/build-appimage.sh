#!/usr/bin/env bash
# Builds Dingus-<version>-x86_64.AppImage: the app (self-contained .NET) plus its own copy of the VLC video engine,
# so it runs on most Linux desktops without installing anything. Run on Ubuntu 22.04 (the oldest base it supports).
#   linux/package/build-appimage.sh <version> <output folder>
set -euo pipefail
VERSION="${1:-0.0.0}"
OUT="$(realpath -m "${2:-out}")"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="$(mktemp -d)"
APPDIR="$WORK/Dingus.AppDir"
mkdir -p "$OUT" "$APPDIR/usr/bin" "$APPDIR/usr/lib" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"

echo "== Publishing Dingus $VERSION"
dotnet publish "$ROOT/linux/Dingus.Linux.csproj" -c Release -r linux-x64 --self-contained true \
  -p:Version="$VERSION" -p:DebugType=none -o "$APPDIR/usr/bin"

echo "== Copying the VLC video engine"
SYS=/usr/lib/x86_64-linux-gnu
cp -a "$SYS"/libvlc.so* "$SYS"/libvlccore.so* "$APPDIR/usr/lib/"
mkdir -p "$APPDIR/usr/lib/vlc"
cp -a "$SYS/vlc/plugins" "$APPDIR/usr/lib/vlc/"
cp -a "$SYS/vlc/libvlc_"*.so* "$APPDIR/usr/lib/vlc/" 2>/dev/null || true
# Leave out what Dingus never uses (streaming out, visualisations, Lua scripts, discs, Qt/skins interfaces).
rm -rf "$APPDIR/usr/lib/vlc/plugins/"{stream_out,mux,access_output,visualization,lua,gui,video_splitter,services_discovery,meta_engine} 2>/dev/null || true
find "$APPDIR/usr/lib/vlc/plugins" -name 'libqt*' -delete || true
find "$APPDIR/usr/lib/vlc/plugins" \( -name '*dvd*' -o -name '*bluray*' -o -name '*cdda*' -o -name '*vcd*' -o -name '*caca*' -o -name '*skins*' \) -delete || true

echo "== Gathering the libraries VLC needs"
LD="$WORK/linuxdeploy-x86_64.AppImage"
curl -sSL -o "$LD" https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-x86_64.AppImage
chmod +x "$LD"
cp "$ROOT/linux/Assets/Dingus-256.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/dingus.png"
cp "$ROOT/linux/Assets/Dingus-256.png" "$APPDIR/dingus.png"
cat > "$APPDIR/usr/share/applications/dingus.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Dingus
Comment=IPTV player – live TV, guide, films and shows
Exec=dingus
Icon=dingus
Categories=AudioVideo;Video;Player;TV;
Terminal=false
StartupWMClass=Dingus
DESKTOP
cp "$APPDIR/usr/share/applications/dingus.desktop" "$APPDIR/dingus.desktop"
LIBS=()
for f in "$APPDIR/usr/lib/"libvlc*.so.* $(find "$APPDIR/usr/lib/vlc" -name '*.so'); do LIBS+=(--library "$f"); done
APPIMAGE_EXTRACT_AND_RUN=1 NO_STRIP=1 "$LD" --appdir "$APPDIR" "${LIBS[@]}" > "$WORK/linuxdeploy.log" 2>&1 || { tail -40 "$WORK/linuxdeploy.log"; exit 1; }
# linuxdeploy copies the plugins flat into usr/lib as well – keep them only in usr/lib/vlc/plugins.
for p in $(find "$APPDIR/usr/lib/vlc/plugins" -name '*.so' -printf '%f\n'); do rm -f "$APPDIR/usr/lib/$p"; done
# Plugin cache: VLC starts faster.
if [ -x "$SYS/vlc/vlc-cache-gen" ]; then "$SYS/vlc/vlc-cache-gen" "$APPDIR/usr/lib/vlc/plugins" || true; fi

cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
export LD_LIBRARY_PATH="$HERE/usr/lib:$HERE/usr/lib/vlc${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export VLC_PLUGIN_PATH="$HERE/usr/lib/vlc/plugins"
exec "$HERE/usr/bin/dingus" "$@"
APPRUN
chmod +x "$APPDIR/AppRun" "$APPDIR/usr/bin/dingus"

echo "== Packing the AppImage"
AT="$WORK/appimagetool-x86_64.AppImage"
curl -sSL -o "$AT" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
chmod +x "$AT"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$AT" --no-appstream "$APPDIR" "$OUT/Dingus-$VERSION-x86_64.AppImage" > "$WORK/appimagetool.log" 2>&1 || { tail -40 "$WORK/appimagetool.log"; exit 1; }
ls -la "$OUT"
