#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build"
SRC="$BUILD/OpenBird-src"
PRODUCTS="$BUILD/products"
OBJROOT="$BUILD/obj"
OUT="$BUILD/artifacts"
AUDIT="$BUILD/audit"
PAYLOAD="$BUILD/Payload"
WRAP="$BUILD/toolwrap"
OPENBIRD_REPO="https://github.com/crosslife/OpenBird.git"
OPENBIRD_COMMIT="9e0198a1a2295f03fa1e8676e216e22c9c7d380b"
SDK="$(xcrun --sdk iphoneos --show-sdk-path)"

rm -rf "$BUILD"
mkdir -p "$BUILD" "$OUT" "$AUDIT" "$WRAP"

# Clone only the reviewed, pinned revision. Git hooks are disabled and the
# remote is removed immediately after checkout so the build cannot follow a
# moving branch or fetch additional code later.
git -c core.hooksPath=/dev/null clone --filter=blob:none --no-checkout "$OPENBIRD_REPO" "$SRC"
git -C "$SRC" -c core.hooksPath=/dev/null checkout --detach "$OPENBIRD_COMMIT"
ACTUAL_COMMIT="$(git -C "$SRC" rev-parse HEAD)"
test "$ACTUAL_COMMIT" = "$OPENBIRD_COMMIT"
git -C "$SRC" remote remove origin

# Neither the game project nor its Cocos2d-x library project may execute an
# Xcode shell-script build phase.
! grep -R -n "PBXShellScriptBuildPhase" \
  "$SRC/proj.ios_mac/FlappyBird.xcodeproj" \
  "$SRC/cocos2d/build/cocos2d_libs.xcodeproj"

# The 2014 project contains a few absolute references to the iOS 7 SDK.
# Convert those references to SDKROOT-relative framework paths.
python3 - "$SRC/proj.ios_mac/FlappyBird.xcodeproj/project.pbxproj" <<'PY'
from pathlib import Path
import re
import sys

path = Path(sys.argv[1])
text = path.read_text(encoding="utf-8")
text = re.sub(
    r"path = Platforms/iPhoneOS\.platform/Developer/SDKs/iPhoneOS7\.0\.sdk/(System/Library/Frameworks/[^;]+); sourceTree = DEVELOPER_DIR;",
    r"path = \1; sourceTree = SDKROOT;",
    text,
)
path.write_text(text, encoding="utf-8")
PY

# Modern iPhoneOS SDK text stubs advertise arm64 only. Clang and ld on the
# runner can still emit ARMv7, so add armv7-ios to every SDK .tbd stub. This is
# the same mechanism already proven by the Breakout32 build, applied broadly
# because Cocos2d-x links several system frameworks and libraries.
sudo python3 - "$SDK" <<'PY'
from pathlib import Path
import json
import plistlib
import re
import sys

sdk = Path(sys.argv[1])
changed = 0
for path in sdk.rglob("*.tbd"):
    try:
        text = path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        continue
    lines = []
    touched = False
    for line in text.splitlines():
        targets = re.match(r"^(\s*targets:\s*\[)([^\]]*)(\].*)$", line)
        if targets:
            values = [item.strip() for item in targets.group(2).split(",") if item.strip()]
            if "armv7-ios" not in values:
                values.insert(0, "armv7-ios")
                touched = True
            line = targets.group(1) + ", ".join(values) + targets.group(3)
        archs = re.match(r"^(\s*archs:\s*\[)([^\]]*)(\].*)$", line)
        if archs:
            values = [item.strip() for item in archs.group(2).split(",") if item.strip()]
            if "armv7" not in values:
                values.insert(0, "armv7")
                touched = True
            line = archs.group(1) + ", ".join(values) + archs.group(3)
        lines.append(line)
    if touched:
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        changed += 1


def patch_arch_values(value, key=""):
    touched = False
    if isinstance(value, dict):
        for child_key in list(value):
            child, child_touched = patch_arch_values(value[child_key], str(child_key))
            value[child_key] = child
            touched = touched or child_touched
    elif isinstance(value, list) and "arch" in key.lower():
        if any(str(item).startswith("arm64") for item in value) and "armv7" not in value:
            value.insert(0, "armv7")
            touched = True
    elif isinstance(value, str) and "arch" in key.lower():
        parts = value.split()
        if any(item.startswith("arm64") for item in parts) and "armv7" not in parts:
            value = "armv7 " + value
            touched = True
    return value, touched

settings_plist = sdk / "SDKSettings.plist"
if settings_plist.exists():
    with settings_plist.open("rb") as f:
        data = plistlib.load(f)
    data, settings_changed = patch_arch_values(data)
    if settings_changed:
        with settings_plist.open("wb") as f:
            plistlib.dump(data, f, fmt=plistlib.FMT_BINARY)

settings_json = sdk / "SDKSettings.json"
if settings_json.exists():
    data = json.loads(settings_json.read_text(encoding="utf-8"))
    data, settings_changed = patch_arch_values(data)
    if settings_changed:
        settings_json.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")

print(f"Patched {changed} text stubs for armv7-ios")
if changed == 0:
    raise SystemExit("No SDK text stubs were patched")
PY

# Xcode 16 rejects armv7 in ARCHS before invoking the compiler. Keep Xcode's
# build graph on a valid arm64 architecture, but route every architecture-aware
# tool through a transparent wrapper that changes the actual target to ARMv7.
# The source files, build phases and linked libraries remain the original ones.
REAL_CLANG="$(xcrun --sdk iphoneos --find clang)"
REAL_CLANGXX="$(xcrun --sdk iphoneos --find clang++)"
REAL_LD="$(xcrun --sdk iphoneos --find ld)"
REAL_LIBTOOL="$(xcrun --find libtool)"
REAL_LIPO="$(xcrun --find lipo)"

python3 - "$WRAP/translate-arch" "$REAL_CLANG" "$REAL_CLANGXX" "$REAL_LD" "$REAL_LIBTOOL" "$REAL_LIPO" <<'PY'
from pathlib import Path
import shlex
import sys

out = Path(sys.argv[1])
clang, clangxx, ld, libtool, lipo = map(shlex.quote, sys.argv[2:])
script = f'''#!/bin/bash
set -euo pipefail
case "$(basename "$0")" in
  clang) REAL={clang} ;;
  clang++) REAL={clangxx} ;;
  ld) REAL={ld} ;;
  libtool) REAL={libtool} ;;
  lipo) REAL={lipo} ;;
  *) echo "Unknown wrapper name: $0" >&2; exit 2 ;;
esac

rewrite_triple() {{
  printf '%s' "$1" | sed -E 's/arm64-apple-ios[0-9]+([.][0-9]+)*/armv7-apple-ios5.0/g'
}}

args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    -arch)
      args+=("-arch")
      shift
      if [[ "${{1:-}}" == "arm64" ]]; then
        args+=("armv7")
      else
        args+=("${{1:-}}")
      fi
      ;;
    -target)
      args+=("-target")
      shift
      args+=("$(rewrite_triple "${{1:-}}")")
      ;;
    -miphoneos-version-min=*)
      args+=("-miphoneos-version-min=5.0")
      ;;
    -iphoneos_version_min)
      args+=("-iphoneos_version_min")
      shift
      args+=("5.0")
      ;;
    -platform_version)
      args+=("-platform_version")
      shift
      args+=("${{1:-ios}}")
      shift
      args+=("5.0")
      shift
      args+=("${{1:-18.5}}")
      ;;
    -mcpu=apple-*)
      # arm64-only CPU selection generated by the modern build system.
      ;;
    arm64)
      args+=("armv7")
      ;;
    *)
      args+=("$(rewrite_triple "$1")")
      ;;
  esac
  shift
done

exec "$REAL" "${{args[@]}}"
'''
out.write_text(script, encoding="utf-8")
out.chmod(0o755)
PY

for tool in clang 'clang++' ld libtool lipo; do
  ln -s translate-arch "$WRAP/$tool"
done

cd "$SRC"
xcodebuild -version
xcodebuild -project proj.ios_mac/FlappyBird.xcodeproj -list

COMMON_SETTINGS=(
  ARCHS=arm64
  VALID_ARCHS=arm64
  EXCLUDED_ARCHS=
  ONLY_ACTIVE_ARCH=NO
  SUPPORTED_PLATFORMS=iphoneos
  IPHONEOS_DEPLOYMENT_TARGET=12.0
  CODE_SIGNING_ALLOWED=NO
  CODE_SIGNING_REQUIRED=NO
  CODE_SIGN_IDENTITY=
  ENABLE_BITCODE=NO
  CLANG_ENABLE_OBJC_ARC=NO
  CLANG_ENABLE_MODULES=NO
  CLANG_CXX_LANGUAGE_STANDARD=gnu++11
  CLANG_CXX_LIBRARY=libc++
  GCC_C_LANGUAGE_STANDARD=gnu99
  GCC_PRECOMPILE_PREFIX_HEADER=NO
  GCC_TREAT_WARNINGS_AS_ERRORS=NO
  GCC_WARN_INHIBIT_ALL_WARNINGS=YES
  COMPILER_INDEX_STORE_ENABLE=NO
  DEBUG_INFORMATION_FORMAT=dwarf
  COPY_PHASE_STRIP=NO
  STRIP_INSTALLED_PRODUCT=NO
  VALIDATE_PRODUCT=NO
  LD_NO_PIE=YES
  PRODUCT_NAME=OpenBird
  PRODUCT_BUNDLE_IDENTIFIER=org.openbird.armv7
  CONFIGURATION_BUILD_DIR="$PRODUCTS"
  OBJROOT="$OBJROOT"
  SYMROOT="$PRODUCTS"
  CC="$WRAP/clang"
  CPLUSPLUS="$WRAP/clang++"
  LD="$WRAP/ld"
  LDPLUSPLUS="$WRAP/clang++"
  LIBTOOL="$WRAP/libtool"
  LIPO="$WRAP/lipo"
)

# Build the real shared scheme so Xcode includes the linked Cocos2d-x,
# CocosDenshion, Lua and Chipmunk products. The wrappers only translate the
# machine architecture passed to the toolchain.
set -o pipefail
xcodebuild \
  -project proj.ios_mac/FlappyBird.xcodeproj \
  -scheme "FlappyBird iOS" \
  -configuration Release \
  -sdk iphoneos \
  build \
  "${COMMON_SETTINGS[@]}" \
  2>&1 | tee "$AUDIT/xcodebuild.log"

APP="$PRODUCTS/OpenBird.app"
test -d "$APP"
test -f "$APP/Info.plist"
test -x "$APP/OpenBird"

rm -rf "$APP/_CodeSignature" "$APP/embedded.mobileprovision"
/usr/libexec/PlistBuddy -c "Set :CFBundleExecutable OpenBird" "$APP/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleIdentifier org.openbird.armv7" "$APP/Info.plist"
/usr/libexec/PlistBuddy -c "Set :MinimumOSVersion 5.0" "$APP/Info.plist" 2>/dev/null || \
  /usr/libexec/PlistBuddy -c "Add :MinimumOSVersion string 5.0" "$APP/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleDisplayName OpenBird32" "$APP/Info.plist" 2>/dev/null || \
  /usr/libexec/PlistBuddy -c "Add :CFBundleDisplayName string OpenBird32" "$APP/Info.plist"

rm -rf "$PAYLOAD"
mkdir -p "$PAYLOAD"
cp -R "$APP" "$PAYLOAD/OpenBird.app"

/usr/bin/file "$APP/OpenBird" | tee "$AUDIT/file.txt"
/usr/bin/lipo -info "$APP/OpenBird" | tee "$AUDIT/lipo.txt"
/usr/bin/otool -L "$APP/OpenBird" | tee "$AUDIT/dylibs.txt"
/usr/bin/nm -u "$APP/OpenBird" | sort -u > "$AUDIT/undefined-symbols.txt"
/usr/bin/otool -l "$APP/OpenBird" > "$AUDIT/load-commands.txt"
find "$APP" -type f -print | sort > "$AUDIT/bundle-files.txt"

/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$PAYLOAD" "$OUT/OpenBird-iPhoneOS5.0-armv7.ipa"
shasum -a 256 "$OUT/OpenBird-iPhoneOS5.0-armv7.ipa" > "$OUT/SHA256SUMS.txt"
cat "$OUT/SHA256SUMS.txt"
