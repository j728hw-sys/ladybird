#!/bin/bash
set -euo pipefail

: "${REAL_XCODEBUILD:?REAL_XCODEBUILD must point to the real xcodebuild binary}"
args=("$@")
is_build=0
for arg in "${args[@]}"; do
  [[ "$arg" == "build" ]] && is_build=1
done

if [[ $is_build -eq 1 ]]; then
  : "${OPENBIRD_ARCH_WRAP:?OPENBIRD_ARCH_WRAP must contain the ARMv7 tool symlinks}"

  # AssetsManager is an optional Cocos2d-x hot-update module. Its source calls
  # system(), which modern iOS SDKs explicitly forbid. OpenBird never references
  # this module, so exclude only that translation unit from the linked extension.
  # The current SDK no longer carries ARMv7 system-library exports. Preserve
  # those imports in the Mach-O so the SideInstaller HLE runtime can resolve them,
  # exactly like the already working Breakout32 build.
  exec "$REAL_XCODEBUILD" "${args[@]}" \
    EXCLUDED_SOURCE_FILE_NAMES=AssetsManager.cpp \
    'OTHER_LDFLAGS=-Wl,-undefined,dynamic_lookup' \
    CC="$OPENBIRD_ARCH_WRAP/clang" \
    CPLUSPLUS="$OPENBIRD_ARCH_WRAP/clang++" \
    LD="$OPENBIRD_ARCH_WRAP/ld" \
    LDPLUSPLUS="$OPENBIRD_ARCH_WRAP/clang++" \
    LIBTOOL="$OPENBIRD_ARCH_WRAP/libtool" \
    LIPO="$OPENBIRD_ARCH_WRAP/lipo"
fi

exec "$REAL_XCODEBUILD" "${args[@]}"
