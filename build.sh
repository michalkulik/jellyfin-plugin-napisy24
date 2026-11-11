#!/usr/bin/env bash
#
# Build and package the Napisy24 plugin.
#
#   ./build.sh net10.0   Jellyfin 12.x (recommended for the current server)
#   ./build.sh net9.0    Jellyfin 10.11.x and 12.x
#
# Produces dist/napisy24_<version>-<tfm>.zip and prints the matching repository
# manifest entry, including the MD5 checksum Jellyfin expects.
set -euo pipefail

PROJECT="Jellyfin.Plugin.Napisy24/Jellyfin.Plugin.Napisy24.csproj"
TFM="${1:-net10.0}"

case "$TFM" in
    net9.0)  TARGET_ABI="10.11.0.0" ;;
    net10.0) TARGET_ABI="12.0.0.0" ;;
    *) echo "Unsupported target framework: $TFM (expected net9.0 or net10.0)" >&2; exit 1 ;;
esac

DOTNET="${DOTNET:-dotnet}"
command -v "$DOTNET" >/dev/null 2>&1 || DOTNET="$HOME/.local/share/tools/dotnet/dotnet"

"$DOTNET" build "$PROJECT" -c Release -f "$TFM" --nologo

VERSION=$(sed -nE 's:.*<AssemblyVersion>([^<]+)</AssemblyVersion>.*:\1:p' "$PROJECT" | head -1)
VERSION="${VERSION:-1.0.0.0}"
OUT="Jellyfin.Plugin.Napisy24/bin/Release/$TFM/Jellyfin.Plugin.Napisy24.dll"

mkdir -p dist
ZIP="dist/napisy24_${VERSION}-${TFM}.zip"
rm -f "$ZIP"

# Jellyfin expects the plugin assembly at the root of the archive.
"${PYTHON:-python3}" - "$OUT" "$ZIP" <<'PY'
import sys, zipfile
dll, dest = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as zf:
    zf.write(dll, "Jellyfin.Plugin.Napisy24.dll")
PY

CHECKSUM=$(md5sum "$ZIP" | cut -d' ' -f1)

cat <<JSON
      {
        "version": "$VERSION",
        "changelog": "Initial version: hash + metadata search",
        "targetAbi": "$TARGET_ABI",
        "sourceUrl": "https://example.invalid/$(basename "$ZIP")",
        "checksum": "$CHECKSUM",
        "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)"
      }
JSON

echo
echo "Package: $ZIP ($(stat -c%s "$ZIP") bytes)"
echo "ABI:     $TFM  ->  targetAbi $TARGET_ABI"
echo "md5:     $CHECKSUM"
