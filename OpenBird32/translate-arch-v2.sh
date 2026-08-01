#!/bin/bash
set -euo pipefail

case "$(basename "$0")" in
  clang) REAL="${OPENBIRD_REAL_CLANG:?}" ;;
  clang++) REAL="${OPENBIRD_REAL_CLANGXX:?}" ;;
  ld) REAL="${OPENBIRD_REAL_LD:?}" ;;
  libtool) REAL="${OPENBIRD_REAL_LIBTOOL:?}" ;;
  lipo) REAL="${OPENBIRD_REAL_LIPO:?}" ;;
  *) echo "Unknown architecture-wrapper name: $0" >&2; exit 2 ;;
esac

temporary_files=()
cleanup() {
  if [[ ${#temporary_files[@]} -gt 0 ]]; then
    rm -f "${temporary_files[@]}"
  fi
}
trap cleanup EXIT

rewrite_triple() {
  printf '%s' "$1" | sed -E 's/arm64-apple-ios[0-9]+([.][0-9]+)*/armv7-apple-ios5.0/g'
}

rewrite_response_file() {
  local source="$1"
  local output
  output="$(mktemp "${TMPDIR:-/tmp}/openbird-armv7-response.XXXXXX")"
  temporary_files+=("$output")
  sed -E \
    -e 's/arm64-apple-ios[0-9]+([.][0-9]+)*/armv7-apple-ios5.0/g' \
    -e 's/-arch[[:space:]]+arm64/-arch armv7/g' \
    -e 's/-miphoneos-version-min=[^[:space:]]+/-miphoneos-version-min=5.0/g' \
    -e 's/-mcpu=apple-[^[:space:]]+//g' \
    "$source" > "$output"
  printf '%s' "$output"
}

args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    @*)
      response_file="${1#@}"
      if [[ -f "$response_file" ]]; then
        args+=("@$(rewrite_response_file "$response_file")")
      else
        args+=("$1")
      fi
      ;;
    -arch|-arch_only)
      args+=("$1")
      shift
      if [[ "${1:-}" == "arm64" ]]; then
        args+=("armv7")
      else
        args+=("${1:-}")
      fi
      ;;
    -target)
      args+=("-target")
      shift
      args+=("$(rewrite_triple "${1:-}")")
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
      args+=("${1:-ios}")
      shift
      args+=("5.0")
      shift
      args+=("${1:-18.5}")
      ;;
    -fobjc-link-runtime)
      # ARC is disabled for this legacy project. The modern driver otherwise
      # tries to inject the removed libarclite archive for iOS 5. Link the
      # ordinary Objective-C runtime stub directly instead.
      args+=("-lobjc")
      ;;
    -mcpu=apple-*)
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

exec "$REAL" "${args[@]}"
