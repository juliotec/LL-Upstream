#!/bin/bash
# rustc's native linker flags can point into the build host's newer glibc.
# Zig supplies the C runtime. Only fontconfig and udev come from the host.
set -euo pipefail
args=()
for arg in "$@"; do
    case "$arg" in
        -fuse-ld=*|-B*|--target=*|-Wl,-O1) ;;
        *) args+=("$arg") ;;
    esac
done
exec "$LONGSHOT_ZIG" cc -target x86_64-linux-gnu.2.27 -Wl,--allow-shlib-undefined \
    -L"$LONGSHOT_NATIVE_LIBS" "${args[@]}"
