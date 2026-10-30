#!/bin/bash
# Builds the Windows app on the Mac: build/windows/pdf-svd-arm64.exe (Windows on ARM, e.g. a VM on Apple Silicon)
# and build/windows/pdf-svd-x64.exe (Intel/AMD PCs). Single files, nothing to install. Requires .NET 10 (brew install dotnet).
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1

scripts/fetch-pdfium-windows.sh >/dev/null
mkdir -p build/windows
# Third-party licenses, embedded in the exe (windows/PdfSvd.csproj) and kept next to it.
dotnet restore windows/PdfSvd.csproj -r win-x64 -v quiet -nologo
python3 -I scripts/collect-licenses-windows.py build/windows/licenses
for ARCH in arm64 x64; do
    OUT="$(mktemp -d)"
    dotnet publish windows/PdfSvd.csproj -c Release -r "win-$ARCH" -o "$OUT" -v quiet -nologo
    cp "$OUT/pdf-svd.exe" "build/windows/pdf-svd-$ARCH.exe"
    rm -rf "$OUT"
done
ls -lh build/windows/*.exe | awk '{print $5, $9}'
