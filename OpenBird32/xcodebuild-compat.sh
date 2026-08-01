#!/bin/bash
set -euo pipefail

: "${REAL_XCODEBUILD:?REAL_XCODEBUILD must point to the real xcodebuild binary}"
args=("$@")
is_build=0
for arg in "${args[@]}"; do
  [[ "$arg" == "build" ]] && is_build=1
done

if [[ $is_build -eq 1 ]]; then
  # AssetsManager is an optional Cocos2d-x hot-update module. Its source calls
  # system(), which modern iOS SDKs explicitly forbid. OpenBird never references
  # this module, so exclude only that translation unit from the linked extension.
  exec "$REAL_XCODEBUILD" "${args[@]}" EXCLUDED_SOURCE_FILE_NAMES=AssetsManager.cpp
fi

exec "$REAL_XCODEBUILD" "${args[@]}"
