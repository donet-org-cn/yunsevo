#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
host="$(uname -s)"
arch="$(uname -m)"
case "$arch" in aarch64|arm64) arch=arm64 ;; x86_64|amd64) arch=x64 ;; *) echo "Unsupported architecture: $arch" >&2; exit 1 ;; esac
case "$host" in Linux) platform=linux ;; Darwin) platform=osx ;; *) echo "Use publish.ps1 on Windows." >&2; exit 1 ;; esac
rid="${1:-$platform-$arch}"
output="${2:-artifacts/v2/$rid}"
export AVALONIA_TELEMETRY_OPTOUT=1
for name in Server Desktop Headless; do
  case "$name" in Server) folder=server ;; Desktop) folder=desktop ;; Headless) folder=headless ;; esac
  dotnet publish "src/MyPan.$name" -c Release -r "$rid" -o "$output/$folder" --configfile NuGet.Config
done
dotnet publish src/MyPan.SmbGateway -c Release -r "$rid" --self-contained true -p:PublishAot=false -o "$output/server/smb" --configfile NuGet.Config
if [[ "$rid" == osx-* ]]; then
  mkdir -p "$output/MyPan.app/Contents/MacOS"
  cp -R "$output/desktop/." "$output/MyPan.app/Contents/MacOS/"
  cp packaging/macos/Info.plist "$output/MyPan.app/Contents/Info.plist"
fi
# tar preserves executable permissions. Package only runtime files and release notices.
mkdir -p "$output/server/downloads"
for kind in desktop headless; do
  source="$output/$kind"
  archive_root="$source"
  prefix=yunsevo-headless
  if [[ "$kind" == desktop ]]; then
    prefix=yunsevo-drive
    if [[ "$rid" == osx-* ]]; then source="$output/MyPan.app"; archive_root="$output"; fi
  fi
  target="$(cd "$output/server/downloads" && pwd)/$prefix-$rid.tar.gz"
  source="$(cd "$source" && pwd)"
  archive_root="$(cd "$archive_root" && pwd)"
  (
    cd "$archive_root"
    find "$source" -type f | while IFS= read -r file; do
      relative="${file#"$source"/}"
      case "$relative" in data/*|credentials/*|state/*|activity/*|*.dSYM/*) continue ;; esac
      name="${file##*/}"
      case "$relative" in Notices/*|*/Notices/*) printf '%s\n' "${file#"$archive_root"/}"; continue ;; esac
      case "$name" in MyPan.Desktop|MyPan.Headless|THIRD-PARTY-NOTICES.md|*.dll|*.so|*.so.[0-9]*|*.dylib|*.plist|*.icns|*.runtimeconfig.json|*.deps.json) printf '%s\n' "${file#"$archive_root"/}" ;; esac
    done | tar -czf "$target.partial" -T -
  )
  mv "$target.partial" "$target"
done

echo "Published $rid to $output. Native AOT server, desktop and headless; managed self-contained SMB gateway."
