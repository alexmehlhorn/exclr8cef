#!/usr/bin/env bash
# Stage native artifacts for the runtime NuGet package of a given RID.
#
#   $ scripts/stage-native-files.sh <rid> <staging-dir>
#
# After CI builds native/ for a platform, this script copies the right
# files into <staging-dir>/runtimes/<rid>/native/ for `dotnet pack` of
# src/runtime/runtime.csproj.
#
# RID -> CEF platform mapping:
#   osx-arm64    macosarm64
#   osx-x64      macosx64
#   win-x64      windows64
#   linux-x64    linux64
#   linux-arm64  linuxarm64

set -euo pipefail

if [ $# -lt 2 ]; then
  echo "usage: $0 <rid> <staging-dir>" >&2
  exit 1
fi

RID="$1"
STAGING="$2"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

case "${RID}" in
  osx-arm64)   CEF_PLATFORM="macosarm64";  SHIM_LIB="libexclr8cef.dylib" ;;
  osx-x64)     CEF_PLATFORM="macosx64";    SHIM_LIB="libexclr8cef.dylib" ;;
  win-x64)     CEF_PLATFORM="windows64";   SHIM_LIB="exclr8cef.dll" ;;
  win-arm64)   CEF_PLATFORM="windowsarm64";SHIM_LIB="exclr8cef.dll" ;;
  linux-x64)   CEF_PLATFORM="linux64";     SHIM_LIB="libexclr8cef.so" ;;
  linux-arm64) CEF_PLATFORM="linuxarm64";  SHIM_LIB="libexclr8cef.so" ;;
  *) echo "unknown RID: ${RID}" >&2; exit 1 ;;
esac

CEF_ROOT="${REPO_ROOT}/third_party/cef/${CEF_PLATFORM}"
CEF_RELEASE="${CEF_ROOT}/Release"
CEF_RESOURCES="${CEF_ROOT}/Resources"
NATIVE_BUILD="${REPO_ROOT}/native/build/shim"
DEST="${STAGING}/runtimes/${RID}/native"

mkdir -p "${DEST}"
echo "Staging ${RID} → ${DEST}"

# --- Shim shared library --------------------------------------------------
if [ -f "${NATIVE_BUILD}/${SHIM_LIB}" ]; then
  cp "${NATIVE_BUILD}/${SHIM_LIB}" "${DEST}/"
elif [ -f "${NATIVE_BUILD}/Release/${SHIM_LIB}" ]; then
  # Multi-config generators (MSVC) put output in Release/.
  cp "${NATIVE_BUILD}/Release/${SHIM_LIB}" "${DEST}/"
else
  echo "shim library not found: ${NATIVE_BUILD}/${SHIM_LIB}" >&2
  exit 1
fi

# --- Per-platform CEF binaries --------------------------------------------
case "${RID}" in
  osx-*)
    # macOS: copy the framework bundle + helper apps.
    cp -R "${CEF_RELEASE}/Chromium Embedded Framework.framework" "${DEST}/"
    ;;
  win-*)
    # Windows: libcef.dll + chrome_elf.dll + ICU + locale paks.
    cp "${CEF_RELEASE}"/*.dll "${DEST}/"
    cp "${CEF_RELEASE}"/*.bin "${DEST}/" 2>/dev/null || true
    cp "${CEF_RELEASE}"/*.dat "${DEST}/" 2>/dev/null || true
    if [ -d "${CEF_RESOURCES}" ]; then
      cp -R "${CEF_RESOURCES}"/* "${DEST}/"
    fi
    ;;
  linux-*)
    # Linux: libcef.so + chrome_sandbox + ICU + locale paks.
    cp "${CEF_RELEASE}"/*.so "${DEST}/" 2>/dev/null || true
    cp "${CEF_RELEASE}"/chrome-sandbox "${DEST}/" 2>/dev/null || true
    cp "${CEF_RELEASE}"/*.bin "${DEST}/" 2>/dev/null || true
    cp "${CEF_RELEASE}"/*.dat "${DEST}/" 2>/dev/null || true
    if [ -d "${CEF_RESOURCES}" ]; then
      cp -R "${CEF_RESOURCES}"/* "${DEST}/"
    fi
    ;;
esac

echo "Staged files:"
find "${DEST}" -maxdepth 2 -type f | head -20
