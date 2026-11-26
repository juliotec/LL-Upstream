#!/bin/bash
# The lint job's tools (docs/ci.md, Forgejo Actions): mise.toml's actionlint, ShellCheck and gitleaks, the Linux x64
# files mise.lock names, each checked by mise.lock's SHA-256 every time it is used, then unpacked into BIN.
#
#     scripts/ci_lint_tools.sh CACHE BIN
#
# CACHE keeps the archives between runs (ci.yml's lint-tools cache): one there that does not check is downloaded again.
# With GITHUB_PATH set, BIN is put on the job's PATH. Nothing of a game is involved.
set -euo pipefail
cd "$(dirname "$0")/.."
CACHE=${1:?usage: ci_lint_tools.sh CACHE BIN}
BIN=${2:?usage: ci_lint_tools.sh CACHE BIN}
mkdir -p "$CACHE" "$BIN"
lock() {   # (mise.lock's file and SHA-256 for linux-x64)
  python3 -I -c 'import sys, tomllib; t = tomllib.load(open("mise.lock", "rb"))["tools"][sys.argv[1]][-1]["platforms.linux-x64"]; print(t["url"], t["checksum"].removeprefix("sha256:"))' "$1"
}
fetch() {   # tool → the checked archive's path
  local url sha file
  read -r url sha < <(lock "$1")
  file=$CACHE/${url##*/}
  if [[ -f $file ]] && echo "$sha  $file" | sha256sum --check --strict --quiet 2> /dev/null; then
    echo "ci_lint_tools: ${url##*/} from the cache" >&2
  else
    echo "ci_lint_tools: downloading ${url##*/}" >&2
    curl -fsSL --retry 5 --retry-delay 5 --proto '=https' -o "$file" "$url"
    echo "$sha  $file" | sha256sum --check --strict --quiet
  fi
  echo "$file"
}
tar -xzf "$(fetch github:rhysd/actionlint)" -C "$BIN" actionlint
tar -xJf "$(fetch github:koalaman/shellcheck)" -C "$BIN" --strip-components=1 --wildcards '*/shellcheck'
tar -xzf "$(fetch gitleaks)" -C "$BIN" gitleaks
"$BIN/actionlint" -version | head -1
"$BIN/shellcheck" --version | sed -n 2p
"$BIN/gitleaks" version
if [[ -n ${GITHUB_PATH:-} ]]; then echo "$BIN" >> "$GITHUB_PATH"; fi
