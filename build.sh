#!/usr/bin/env sh
# Builds the plugin with the official .NET SDK image, so nothing needs installing on the host.
# Output: ./dist/Jellyfin.Plugin.Wizarr.dll
set -e
cd "$(dirname "$0")"
docker run --rm \
  -v "$PWD":/src -w /src \
  mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet publish Jellyfin.Plugin.Wizarr/Jellyfin.Plugin.Wizarr.csproj -c Release -o /src/dist
# The container writes as root; hand the files back to you.
sudo chown -R "$(id -u):$(id -g)" dist 2>/dev/null || true
echo "Built: dist/Jellyfin.Plugin.Wizarr.dll"
