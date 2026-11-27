#!/bin/bash
# Fetch only the Tooling commit named by the selected source checkout.
set -euo pipefail
checkout=${1:?usage: ci_tooling.sh DESTINATION}
pin=$(cat .tooling-rev)
[[ "$pin" =~ ^[0-9a-f]{40}$ ]]
git init -q "$checkout"
git -C "$checkout" fetch -q --depth 1 https://git.magicbean.dev/MagicBeanDev/Tooling.git "$pin"
git -C "$checkout" checkout -q --detach FETCH_HEAD
