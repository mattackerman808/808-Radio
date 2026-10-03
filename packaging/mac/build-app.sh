#!/bin/bash
# Builds "808 Radio.app" for macOS (Apple Silicon): self-contained .NET publish, the native libraries, an icon, the
# Info.plist, code signing with the hardened runtime, and (with credentials) notarization and stapling. Output:
# dist/808Radio-v<version>-mac-arm64.zip, ready to download and run with nothing installed.
#
#   packaging/mac/build-app.sh                       ad-hoc signed (runs here; other Macs would warn)
#   SIGN_IDENTITY="Developer ID Application: Name (TEAMID)" packaging/mac/build-app.sh
#   SIGN_IDENTITY="..." NOTARY_PROFILE=808Radio packaging/mac/build-app.sh      also notarizes and staples
#
# One-time setup for signing: a "Developer ID Application" certificate in the login keychain (Xcode > Settings >
# Accounts > Manage Certificates, or developer.apple.com). For notarization, store credentials once:
#   xcrun notarytool store-credentials 808Radio --apple-id <you@apple-id> --team-id <TEAMID> --password <app-specific password>
set -euo pipefail
cd "$(dirname "$0")/../.."
root=$(pwd)
export PATH="$HOME/.dotnet:$PATH"
version=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' src/Radio808.Avalonia/Radio808.Avalonia.csproj)
rid=${RID:-osx-arm64}
dist="$root/dist"
app="$dist/808 Radio.app"
contents="$app/Contents"
identity=${SIGN_IDENTITY:--}
echo "== 808 Radio $version for $rid (signing: $identity)"

# ---- native libraries
[ -f native/bin/libnrsc5.dylib ] && [ -f native/bin/librtlsdr.dylib ] && [ -f native/bin/libr808audio.dylib ] || native/mac/build-native-mac.sh

# ---- publish
mkdir -p "$dist"; pkill -f "808 Radio.app/Contents/MacOS/808Radio" 2>/dev/null && sleep 2 || true   # a running copy would lose its files
rm -rf "$dist/publish" "$app"
dotnet publish src/Radio808.Avalonia -c Release -r "$rid" --self-contained -p:PublishSingleFile=false -p:DebugType=none \
    -o "$dist/publish" > "$dist/publish.log" 2>&1 || { tail -20 "$dist/publish.log"; exit 1; }
for f in 808Radio libnrsc5.dylib librtlsdr.dylib libr808audio.dylib; do [ -f "$dist/publish/$f" ] || { echo "publish is missing $f"; exit 1; }; done

# ---- bundle
mkdir -p "$contents/MacOS" "$contents/Resources"
cp -R "$dist/publish/." "$contents/MacOS/"
sed "s/__VERSION__/$version/g" packaging/mac/Info.plist > "$contents/Info.plist"
printf 'APPL????' > "$contents/PkgInfo"
# icon: an iconset from the largest PNG we have
iconset="$dist/808Radio.iconset"; rm -rf "$iconset"; mkdir -p "$iconset"
src=src/Radio808.App/app.png
for s in 16 32 64 128 256 512; do
    sips -z $s $s "$src" --out "$iconset/icon_${s}x${s}.png" > /dev/null
    d=$((s * 2)); sips -z $d $d "$src" --out "$iconset/icon_${s}x${s}@2x.png" > /dev/null
done
iconutil -c icns "$iconset" -o "$contents/Resources/808Radio.icns"

# ---- sign: every file inside first (a .NET app's managed assemblies count as code too), then the bundle;
#      the hardened runtime needs the .NET entitlements
ent=packaging/mac/entitlements.plist
opts=(--force --options runtime --entitlements "$ent" --sign "$identity")
[ "$identity" != "-" ] && opts+=(--timestamp)
: > "$dist/sign.log"
# (the main executable last: signing it makes codesign check the bundle's other files, which must already be signed)
find "$contents/MacOS" -type f ! -name 808Radio | while read -r f; do
    codesign "${opts[@]}" "$f" 2>> "$dist/sign.log" || { echo "signing failed: $f"; tail -3 "$dist/sign.log"; exit 1; }
done
codesign "${opts[@]}" --identifier org.808.radio "$contents/MacOS/808Radio" 2>> "$dist/sign.log" || { echo "signing the executable failed"; tail -3 "$dist/sign.log"; exit 1; }
codesign "${opts[@]}" --identifier org.808.radio "$app" 2>> "$dist/sign.log" || { echo "signing the bundle failed"; tail -3 "$dist/sign.log"; exit 1; }
codesign --verify --deep --strict "$app" && echo "signature ok"

# ---- notarize and staple
if [ -n "${NOTARY_PROFILE:-}" ]; then
    echo "== notarizing"
    ditto -c -k --keepParent "$app" "$dist/notarize.zip"
    xcrun notarytool submit "$dist/notarize.zip" --keychain-profile "$NOTARY_PROFILE" ${NOTARY_KEYCHAIN:+--keychain "$NOTARY_KEYCHAIN"} --wait
    xcrun stapler staple "$app"
    rm -f "$dist/notarize.zip"
    spctl -a -vv "$app" 2>&1 | tail -2
fi

# ---- the download
zip="$dist/808Radio-v$version-mac-${rid#osx-}.zip"
rm -f "$zip"
ditto -c -k --keepParent "$app" "$zip"
echo "== $zip ($(du -h "$zip" | cut -f1))"
