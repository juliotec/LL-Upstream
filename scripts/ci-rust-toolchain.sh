#!/bin/bash
# Install the Linux and Windows build dependencies on an Ubuntu/Debian CI runner.
# The caller adds ~/.cargo/bin to PATH when installing rustup for the first time.
set -euo pipefail
sudo=()
if (( EUID != 0 )); then sudo=(sudo); fi
"${sudo[@]}" apt-get update -qq
"${sudo[@]}" apt-get install -y --no-install-recommends curl ca-certificates pkg-config build-essential \
    libudev-dev libfontconfig1-dev libxkbcommon-dev xz-utils python3 file
export PATH="${CARGO_HOME:-$HOME/.cargo}/bin:$HOME/.cargo/bin:$PATH"
toolchain=$(python3 -c 'import tomllib; print(tomllib.load(open("rust-toolchain.toml", "rb"))["toolchain"]["channel"])')
if ! command -v rustup >/dev/null; then
    installer_dir=$(mktemp -d)
    installer=$installer_dir/rustup-init
    trap 'rm -rf "$installer_dir"' EXIT
    curl -fsSL --retry 5 https://static.rust-lang.org/rustup/archive/1.29.2/x86_64-unknown-linux-gnu/rustup-init -o "$installer"
    echo "5be191e8c70a5007074254cb74d78dbfdd25cd5a61c27de7b8e5410122ec24f4  $installer" | sha256sum -c -
    chmod +x "$installer"
    "$installer" -y --profile minimal --default-toolchain "$toolchain" --no-modify-path
fi
rustup toolchain install "$toolchain" --profile minimal
rustup target add --toolchain "$toolchain" x86_64-pc-windows-gnullvm
