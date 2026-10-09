#!/bin/bash
# Builds the Apple TV app as an App Store package and uploads it to App Store Connect (TestFlight picks it up).
#
#   src/Radio808.TV/publish.sh <build-number>
#
# Needs, once:
#   - an Apple Distribution certificate in the keychain (Xcode > Settings > Accounts > Manage Certificates > +)
#   - an App Store provisioning profile for org.808.radio.tv (tvOS) installed (Xcode downloads it, or
#     developer.apple.com > Profiles); its name goes in CODESIGN_PROVISION below
#   - an App Store Connect API key (App Store Connect > Users and Access > Integrations > App Store Connect API):
#     the .p8 in ~/.appstoreconnect/private_keys/AuthKey_<KEY_ID>.p8, and ASC_KEY_ID / ASC_ISSUER_ID exported
#   - native/tvos/build-native-tvos.sh run before (libnrsc5.a)
set -euo pipefail
cd "$(dirname "$0")"
build=${1:?build number (must go up with every upload)}
: "${CODESIGN_KEY:=Apple Distribution: 808 Heavy Industries LLC (HK6G8MB547)}"
: "${CODESIGN_PROVISION:=tvOS Team Store Provisioning Profile: org.808.radio.tv}"
: "${ASC_KEY_ID:?App Store Connect API key id}"
: "${ASC_ISSUER_ID:?App Store Connect API issuer id}"
version=$(sed -n 's|.*<ApplicationDisplayVersion>\(.*\)</ApplicationDisplayVersion>.*|\1|p' Radio808.TV.csproj)

echo "== 808 Radio for Apple TV $version ($build): building"
~/.dotnet/dotnet publish -r tvos-arm64 -c Release -p:ArchiveOnBuild=true -p:BuildIpa=true \
    -p:ApplicationVersion="$build" \
    -p:CodesignKey="$CODESIGN_KEY" -p:CodesignProvision="$CODESIGN_PROVISION" | grep -E "error|Created the package" || true
ipa=bin/Release/net9.0-tvos/tvos-arm64/publish/808Radio.ipa
[ -f "$ipa" ] || { echo "no .ipa was built"; exit 1; }

echo "== validating"
xcrun altool --validate-app -f "$ipa" -t tvos --apiKey "$ASC_KEY_ID" --apiIssuer "$ASC_ISSUER_ID"
echo "== uploading"
xcrun altool --upload-app -f "$ipa" -t tvos --apiKey "$ASC_KEY_ID" --apiIssuer "$ASC_ISSUER_ID"
echo "done: App Store Connect processes it for a few minutes, then it appears under TestFlight"
