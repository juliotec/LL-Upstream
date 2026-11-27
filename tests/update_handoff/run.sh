#!/bin/bash
# Run the updater shared by v1.3.0 and v1.3.1 on Linux and inside Windows under Wine.
# The only private key involved is the repository's public test fixture.
set -euo pipefail
tag=${1:?expected a test tag}
dist=$(cd "${2:?expected the release package directory}" && pwd)
scratch=${3:?expected a temporary directory}
mkdir -p "$scratch"
scratch=$(cd "$scratch" && pwd)
repo=$(cd "$(dirname "$0")/../.." && pwd)
fixture="$repo/tests/fixtures/release"
previous="$scratch/previous-stable-v1.3.1"
if [[ ! -d "$previous" ]]; then
    git clone -q --depth 1 --branch v1.3.1 https://git.magicbean.dev/MagicBeanDev/LongshotLauncher.git "$previous"
fi
newest=23a9e6d5625329487ba279a9b0c38b1195da527c
earlier=3af691ed6deedba39d3776bf7a7e80f2dbc09fca
test "$(git -C "$previous" rev-parse HEAD)" = "$newest"
git -C "$previous" fetch -q --depth 1 origin refs/tags/v1.3.0:refs/tags/v1.3.0
test "$(git -C "$previous" rev-parse 'v1.3.0^{commit}')" = "$earlier"
# v1.3.1 only changed stable build registration and leftover detection in Core.
# Verify all remaining Core sources before sharing the expensive handoff test.
git -C "$previous" diff --exit-code "$earlier" "$newest" -- Core \
    ':(exclude)Core/BuildRegistry.cs' ':(exclude)Core/Leftovers.cs'
export APPIMAGE_EXTRACT_AND_RUN=1 WINEDEBUG=-all WINEPREFIX="$scratch/wine"
export HANDOFF_GUI_TEST="$repo/tests/test_rust_window.py"
project="$repo/tests/update_handoff/Handoff.csproj"
dotnet run --project "$project" -p:StableCore="$previous/Core" -- "$tag" "$dist" "$fixture/test-key.pem" "$scratch/native"
dotnet publish "$project" -p:StableCore="$previous/Core" --self-contained -r win-x64 \
    -p:NuGetLockFilePath="$scratch/win-runtime.lock.json" -o "$scratch/win-harness"
# winepath can initialize the prefix too. Keep all Wine processes on this display.
xvfb-run -a bash -se -- "$scratch" "$tag" "$dist" "$fixture" <<'WINDOWS'
scratch=$1
# Only skip Mono/Gecko during unattended prefix setup. The self-contained .NET
# runtime subsequently needs Wine's normal PE loader for its managed assemblies.
WINEDLLOVERRIDES='mscoree,mshtml=d' wineboot --init
for variable in HANDOFF_ORIGINAL_WINDOWS HANDOFF_ORIGINAL_LINUX; do
    if [[ -n "${!variable:-}" ]]; then
        printf -v "$variable" '%s' "$(winepath -w "${!variable}")"
        export "${variable?}"
    fi
done
wine "$scratch/win-harness/Handoff.exe" "$2" "$(winepath -w "$3")" \
    "$(winepath -w "$4/test-key.pub.pem")" "$(winepath -w "$scratch/windows")" \
    "$(winepath -w "$scratch/native/signed-fixture")"
WINDOWS
