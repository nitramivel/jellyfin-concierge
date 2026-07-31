#!/usr/bin/env bash
# Builds the plugin and assembles a deployable folder for a Jellyfin 10.11.x
# plugin directory (e.g. /config/plugins/Concierge_<version> in the container).
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.1.0.0}"
TARGET_ABI="${TARGET_ABI:-10.11.0.0}"
OUT="artifacts/Concierge_${VERSION}"

dotnet build Jellyfin.Plugin.Concierge/Jellyfin.Plugin.Concierge.csproj -c Release -p:Version="${VERSION%.*}"

rm -rf "$OUT"
mkdir -p "$OUT"
cp Jellyfin.Plugin.Concierge/bin/Release/net9.0/Jellyfin.Plugin.Concierge.dll "$OUT/"

cat > "$OUT/meta.json" <<EOF
{
  "category": "General",
  "changelog": "",
  "description": "Natural-language search for your Jellyfin library.",
  "guid": "361b0830-e7c9-460a-b116-0164adec76dd",
  "name": "Concierge",
  "overview": "Search your library the way you'd describe a film to a friend.",
  "owner": "nitramivel",
  "targetAbi": "${TARGET_ABI}",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": ""
}
EOF

echo "Packaged: $OUT"
