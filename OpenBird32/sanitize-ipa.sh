#!/bin/bash
set -euo pipefail

IPA="${1:?usage: sanitize-ipa.sh path/to/OpenBird.ipa}"
AUDIT_DIR="${2:?usage: sanitize-ipa.sh IPA audit-directory}"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/openbird-sanitize.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$AUDIT_DIR"
ditto -x -k "$IPA" "$WORK"
APP="$WORK/Payload/OpenBird.app"
test -d "$APP"

# The upstream Xcode project copies ZeroBrane's optional Lua remote debugger,
# but OpenBird's main.lua never requires it. Remove it from the distributed IPA.
rm -f "$APP/mobdebug.lua"
test ! -e "$APP/mobdebug.lua"

# Keep the package unsigned for the SideInstaller/HLE test path.
rm -rf "$APP/_CodeSignature" "$APP/embedded.mobileprovision"

rm -f "$IPA"
ditto -c -k --sequesterRsrc --keepParent "$WORK/Payload" "$IPA"
find "$APP" -type f -print | sort > "$AUDIT_DIR/bundle-files.txt"
! grep -q '/mobdebug.lua$' "$AUDIT_DIR/bundle-files.txt"
shasum -a 256 "$IPA" > "$(dirname "$IPA")/SHA256SUMS.txt"
