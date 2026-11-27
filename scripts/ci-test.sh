#!/bin/bash
set -euo pipefail
cargo fmt --all -- --check
cargo clippy --locked --workspace --all-targets -- -D warnings
python3 -I tests/test_launcher_core.py "$@"
