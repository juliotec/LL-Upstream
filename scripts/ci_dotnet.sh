#!/bin/bash
# The .NET SDK for CI (docs/ci.md, Forgejo Actions): global.json's version, Microsoft's Linux x64 archive, checked by the
# SHA-512 Microsoft publishes for it (pinned below) every time it is used, then unpacked into DEST.
#
#     scripts/ci_dotnet.sh CACHE DEST
#
# CACHE keeps the archive between runs (the workflows' dotnet-sdk cache): an archive there that does not check is
# downloaded again, so a cache can cost a download, never change the SDK. With GITHUB_ENV set, the job's later steps get
# DOTNET_ROOT, the SDK on PATH, and .NET's first-run noise off. Nothing of a game is involved.
set -euo pipefail
cd "$(dirname "$0")/.."
CACHE=${1:?usage: ci_dotnet.sh CACHE DEST}
DEST=${2:?usage: ci_dotnet.sh CACHE DEST}
# (global.json's version; a change to it changes these two lines: Microsoft's releases.json for the channel,
# https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json, has the archive's hash)
VERSION=10.0.404
SHA512=577d0eb17fec33d029104c5c63a1ab0accc3e28776274abef2218dbac5af18d2109b5406d26b31d76f5c245215e3ba0926a94c20246c672c41847c9e1eb1dc50
want=$(python3 -I -c 'import json; print(json.load(open("global.json"))["sdk"]["version"])')
[[ $want == "$VERSION" ]] || { echo "::error::global.json asks for .NET SDK $want, scripts/ci_dotnet.sh pins $VERSION: update its VERSION and SHA512"; exit 1; }
archive=$CACHE/dotnet-sdk-$VERSION-linux-x64.tar.gz
mkdir -p "$CACHE" "$DEST"
if [[ -f $archive ]] && echo "$SHA512  $archive" | sha512sum --check --strict --quiet 2> /dev/null; then
  echo "ci_dotnet: .NET SDK $VERSION's archive from the cache"
else
  echo "ci_dotnet: downloading .NET SDK $VERSION (no checked archive in the cache)"
  curl -fsSL --retry 5 --retry-delay 5 --proto '=https' -o "$archive" \
    "https://builds.dotnet.microsoft.com/dotnet/Sdk/$VERSION/dotnet-sdk-$VERSION-linux-x64.tar.gz"
  echo "$SHA512  $archive" | sha512sum --check --strict --quiet
fi
tar -xzf "$archive" -C "$DEST"
"$DEST/dotnet" --version
if [[ -n ${GITHUB_ENV:-} ]]; then
  {
    echo "DOTNET_ROOT=$DEST"
    echo "DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "DOTNET_NOLOGO=1"
    echo "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
  } >> "$GITHUB_ENV"
  echo "$DEST" >> "${GITHUB_PATH:?}"
fi
