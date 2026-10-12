#!/bin/bash
set -euo pipefail
RID="${1:?runtime identifier}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CACHE="$ROOT/.ads-ios"
if [ "$RID" = "iossimulator-arm64" ]; then
  DEST="$CACHE/iphonesimulator"
else
  DEST="$CACHE/iphoneos"
fi
if [ -d "$DEST/GoogleMobileAds.framework" ] && [ -d "$DEST/UserMessagingPlatform.framework" ]; then
  exit 0
fi
mkdir -p "$CACHE"
download() {
  local name="$1"
  local url="$2"
  local zip="$CACHE/$name"
  if [ ! -f "$zip" ]; then
    curl -L --fail --retry 3 -o "$zip.partial" "$url"
    mv "$zip.partial" "$zip"
  fi
  echo "$zip"
}
ADS="$(download GoogleMobileAds-13.2.0.zip \
  "https://dl.google.com/googleadmobadssdk/448fc380973105a3/googlemobileadsios-spm-13.2.0.zip")"
UMP="$(download UserMessagingPlatform-3.1.0.zip \
  "https://dl.google.com/googleadmobadssdk/90fe6bf3b0f4ce0d/googleusermessagingplatformios-spm-3.1.0.zip")"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
unzip -q "$ADS" -d "$STAGE/ads"
unzip -q "$UMP" -d "$STAGE/ump"
rm -rf "$DEST"
mkdir -p "$DEST"
while IFS= read -r xcf; do
  slice=""
  if [ "$RID" = "iossimulator-arm64" ]; then
    for candidate in ios-arm64-simulator ios-arm64_x86_64-simulator; do
      if [ -d "$xcf/$candidate" ]; then
        slice="$xcf/$candidate"
        break
      fi
    done
  elif [ -d "$xcf/ios-arm64" ]; then
    slice="$xcf/ios-arm64"
  fi
  if [ -n "$slice" ]; then
    find "$slice" -maxdepth 3 -type d -name '*.framework' -exec cp -R {} "$DEST/" \;
  fi
done < <(find "$STAGE" -type d -name '*.xcframework')
test -d "$DEST/GoogleMobileAds.framework"
test -d "$DEST/UserMessagingPlatform.framework"
