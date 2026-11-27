#!/bin/bash
# Build the launcher downloads and legacy updater aliases from an x86_64 Linux host.
#   scripts/package.sh <tag> <out folder>
# The Windows setup and portable zip contain the same self-contained executable.
# Windows uses Longshot's pinned LLVM-MinGW and the x86_64-pc-windows-gnullvm Rust target.
# LONGSHOT_LLVM_MINGW can select an existing unpacked copy. Linux needs fontconfig
# and libudev development libraries. Its release linker targets glibc 2.27.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
TAG=${1:?usage: package.sh <tag> <out folder>}
OUT=${2:?usage: package.sh <tag> <out folder>}
# (the same pins as Longshot's scripts/linux/deps.sh)
APPIMAGETOOL_VERSION=1.9.1
APPIMAGETOOL_SHA256=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
APPIMAGE_RUNTIME_VERSION=20251108
APPIMAGE_RUNTIME_SHA256=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d
[[ $TAG =~ ^[0-9A-Za-z._+-]{1,128}$ ]] || { echo "not a version name: $TAG" >&2; exit 1; }
mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)
WORK=$(mktemp -d "$OUT/../.launcher-package.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
TOOLS="$OUT/../.appimagetool"

# Publish downloads to the cache only after checking their contents.
verified_download() {
    local archive=$1 url=$2 digest=$3
    mkdir -p "$TOOLS"
    if ! [[ -f "$TOOLS/$archive" ]] || ! echo "$digest  $TOOLS/$archive" | sha256sum --quiet -c -; then
        curl -fsSL --retry 5 "$url" -o "$WORK/$archive"
        echo "$digest  $WORK/$archive" | sha256sum -c - >&2
        mv "$WORK/$archive" "$TOOLS/$archive"
    fi
}

unpack() {
    verified_download "$@"
    tar -C "$WORK" -xf "$TOOLS/$1"
}

# Use the same Windows compiler as Longshot's Linux builder (scripts/builder_package.py).
prepare_windows_toolchain() {
    local archive=llvm-mingw-20261104-ucrt-ubuntu-22.04-x86_64
    local mingw=${LONGSHOT_LLVM_MINGW:-$WORK/$archive}
    if [[ -z "${LONGSHOT_LLVM_MINGW:-}" ]]; then
        unpack "$archive.tar.xz" "https://github.com/mstorsjo/llvm-mingw/releases/download/20261104/$archive.tar.xz" \
            919380403de87dca3c581c2053230cd298af5e891286628ba0613b7fed370926
    fi
    [[ -x "$mingw/bin/x86_64-w64-mingw32-clang" ]] || { echo "Missing LLVM-MinGW in $mingw" >&2; exit 1; }
    export PATH="$mingw/bin:$PATH"
}

# Link against the same glibc baseline as v1.3.0, even on a newer build host.
prepare_linux_linker() {
    LONGSHOT_ZIG=${ZIG:-}
    if [[ -z "$LONGSHOT_ZIG" ]]; then
        local archive=zig-x86_64-linux-0.17.0
        unpack "$archive.tar.xz" "https://ziglang.org/download/0.17.0/$archive.tar.xz" \
            1cbe9df9f27e6b78d14ccbca43b6703a404ef79ef1c463de901d7f088d4e2026
        LONGSHOT_ZIG="$WORK/$archive/zig"
    fi
    [[ $("$LONGSHOT_ZIG" version) == 0.17.0 ]] || { echo "Packaging needs Zig 0.17.0." >&2; exit 1; }
    export LONGSHOT_ZIG
    LONGSHOT_NATIVE_LIBS="$WORK/native-libs"
    export LONGSHOT_NATIVE_LIBS
    mkdir -p "$LONGSHOT_NATIVE_LIBS"
    ln -s "$(pkg-config --variable=libdir fontconfig)/libfontconfig.so" "$LONGSHOT_NATIVE_LIBS/"
    ln -s "$(pkg-config --variable=libdir libudev)/libudev.so" "$LONGSHOT_NATIVE_LIBS/"
}

publish() {
    local rid=$1 dir=$2
    local target program rustflags=()
    case "$rid" in
        win-x64)
            target=x86_64-pc-windows-gnullvm
            prepare_windows_toolchain
            program=LongshotLauncher.exe
            rustflags=(-C target-feature=+crt-static) ;;
        linux-x64)
            target=x86_64-unknown-linux-gnu; program=LongshotLauncher
            prepare_linux_linker
            rustflags=(-C "linker=$ROOT/scripts/linux-release-linker.sh") ;;
    esac
    local target_dir=${CARGO_TARGET_DIR:-$ROOT/target}
    LONGSHOT_TAG="$TAG" cargo rustc --manifest-path "$ROOT/Cargo.toml" --bin LongshotLauncher \
        --locked --release --target-dir "$target_dir" --target "$target" -- "${rustflags[@]}"
    mkdir -p "$dir"
    cp "$target_dir/$target/release/$program" "$dir/$program"
    if [[ "$rid" == win-x64 ]]; then
        imports=$(objdump -p "$dir/$program" | awk '/DLL Name:/ {print tolower($NF)}')
        if grep -Eq '^(libunwind|libwinpthread|libgcc_s|libstdc\+\+)' <<<"$imports"; then
            echo "Windows launcher needs a compiler runtime DLL: $imports" >&2
            exit 1
        fi
    else
        local newest
        newest=$(readelf --version-info "$dir/$program" | grep -oE 'GLIBC_[0-9.]+' | sort -Vu | tail -1)
        [[ $(printf '%s\n' GLIBC_2.27 "$newest" | sort -V | tail -1) == GLIBC_2.27 ]] || {
            echo "Linux launcher requires $newest, above the v1.3.0 baseline." >&2; exit 1;
        }
    fi
}

publish win-x64 "$WORK/win-x64"
cp "$WORK/win-x64/LongshotLauncher.exe" "$OUT/LongshotLauncher-$TAG-windows-x64.exe"
cp "$OUT/LongshotLauncher-$TAG-windows-x64.exe" "$OUT/LongshotLauncher-$TAG-windows-x64-setup.exe"
# Older portable launchers extract the executable and retain their existing marker.
python3 -I - "$WORK/win-x64" "$OUT/LongshotLauncher-$TAG-windows-x64-portable.zip" <<'PYZIP'
import sys, zipfile
src, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    name = "LongshotLauncher.exe"
    info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o100644 << 16
    with open(src + "/" + name, "rb") as f:
        z.writestr(info, f.read())
PYZIP

publish linux-x64 "$WORK/linux-x64"
app="$WORK/AppDir"
mkdir -p "$app/usr/bin"
cp "$WORK/linux-x64/LongshotLauncher" "$app/usr/bin/LongshotLauncher"
cp "$ROOT/assets/longshot.png" "$app/longshot-launcher.png"
cat > "$app/longshot-launcher.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Longshot Launcher
Comment=Install Longshot's builder, build the game from your own copy, and play
Exec=LongshotLauncher
Icon=longshot-launcher
Categories=Game;
Terminal=false
StartupWMClass=longshot-launcher
EOF
cat > "$app/AppRun" <<'EOF'
#!/bin/sh
here=$(dirname "$(readlink -f "$0")")
exec "$here/usr/bin/LongshotLauncher" "$@"
EOF
chmod 755 "$app/AppRun" "$app/usr/bin/LongshotLauncher"
"$app/usr/bin/LongshotLauncher" --self-check | grep -qxF "LongshotLauncher $TAG" ||
    { echo "the Linux launcher does not answer --self-check with $TAG" >&2; exit 1; }

tool=${APPIMAGETOOL:-}
runtime=${APPIMAGE_RUNTIME:-}
if [[ -z "$tool" ]]; then
    name="appimagetool-$APPIMAGETOOL_VERSION"
    verified_download "$name" \
        "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-x86_64.AppImage" "$APPIMAGETOOL_SHA256"
    tool="$TOOLS/$name"
    chmod 755 "$tool"
fi
if [[ -z "$runtime" ]]; then
    name="runtime-x86_64-$APPIMAGE_RUNTIME_VERSION"
    verified_download "$name" \
        "https://github.com/AppImage/type2-runtime/releases/download/$APPIMAGE_RUNTIME_VERSION/runtime-x86_64" "$APPIMAGE_RUNTIME_SHA256"
    runtime="$TOOLS/$name"
fi
# (no update information: the launcher updates itself from the signed release files, not with AppImageUpdate)
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream --runtime-file "$runtime" "$app" \
    "$OUT/LongshotLauncher-$TAG-linux-x64.AppImage" >&2
chmod 755 "$OUT/LongshotLauncher-$TAG-linux-x64.AppImage"
cp -p "$OUT/LongshotLauncher-$TAG-linux-x64.AppImage" "$OUT/LongshotLauncher-$TAG-x86_64.AppImage"
ls -l "$OUT"
