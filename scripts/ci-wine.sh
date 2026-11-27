#!/bin/bash
# WineHQ's Noble build supports the Windows CNG calls used by the .NET stable updater.
set -euo pipefail
version='11.22~noble-1'
base=https://dl.winehq.org/wine-builds
sudo=()
if (( EUID != 0 )); then sudo=(sudo); fi

key=$(mktemp)
trap 'rm -f "$key"' EXIT
curl -fsSL --retry 5 "$base/winehq.key" -o "$key"
echo "d965d646defe94b3dfba6d5b4406900ac6c81065428bf9d9303ad7a72ee8d1b8  $key" | sha256sum -c -
"${sudo[@]}" install -d -m 0755 /etc/apt/keyrings
"${sudo[@]}" install -m 0644 "$key" /etc/apt/keyrings/longshot-winehq.asc
"${sudo[@]}" dpkg --add-architecture i386
echo "deb [arch=amd64,i386 signed-by=/etc/apt/keyrings/longshot-winehq.asc] $base/ubuntu noble main" |
    "${sudo[@]}" tee /etc/apt/sources.list.d/longshot-winehq.list >/dev/null
"${sudo[@]}" apt-get update -qq
# Wine loads GnuTLS at runtime for the x64 harness's BCrypt verification.
"${sudo[@]}" apt-get install -y --no-install-recommends "winehq-devel=$version" libgnutls30t64
wine --version | grep -Fx wine-11.22
