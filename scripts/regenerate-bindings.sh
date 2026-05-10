#!/usr/bin/env bash
# Regenerate C# P/Invoke bindings from the shim's C ABI header
# (native/shim/exclr8cef.h) into managed/Exclr8Cef.WebView/Generated/.
#
# Run this after changing the C ABI surface or when bumping CEF.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ_DIR="${REPO_ROOT}/managed/Exclr8Cef"

cd "${PROJ_DIR}"
mkdir -p Generated

# ClangSharpPInvokeGenerator depends on libclang.dylib + libClangSharp.dylib,
# which are shipped in the runtime NuGet package
# (clangsharppinvokegenerator.<rid>). The tool's [DllImport("libclang")] uses
# bare dlopen, so we have to point DYLD_FALLBACK_LIBRARY_PATH at the package
# directory. SIP doesn't filter this for user processes.
case "$(uname -s)/$(uname -m)" in
  Darwin/arm64)  RID="osx-arm64" ;;
  Darwin/x86_64) RID="osx-x64" ;;
  Linux/x86_64)  RID="linux-x64" ;;
  Linux/aarch64) RID="linux-arm64" ;;
  *) RID="" ;;
esac

if [ -n "${RID}" ]; then
  LIBCLANG_DIR=$(find "${HOME}/.nuget/packages/clangsharppinvokegenerator.${RID}" \
                      -name "libclang.dylib" -o -name "libclang.so.*" \
                      2>/dev/null | head -1 | xargs -I{} dirname {})
  if [ -n "${LIBCLANG_DIR}" ]; then
    export DYLD_FALLBACK_LIBRARY_PATH="${LIBCLANG_DIR}:${DYLD_FALLBACK_LIBRARY_PATH:-/usr/local/lib:/usr/lib}"
    export LD_LIBRARY_PATH="${LIBCLANG_DIR}:${LD_LIBRARY_PATH:-}"
  fi
fi

# ClangSharpPInvokeGenerator is installed as a local tool in managed/.
cd "${REPO_ROOT}/managed"
dotnet ClangSharpPInvokeGenerator @"${PROJ_DIR}/generate-bindings.rsp"

echo
echo "Bindings regenerated at: managed/Exclr8Cef/Generated/"
ls -l "${PROJ_DIR}/Generated/"
