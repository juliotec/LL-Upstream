# Launcher CI

Forgejo checks pull requests, pushes to `main`, and manual runs. The path check reads the base revision on a pull request. Changes confined to documentation skip the build. The lint job checks the workflows, shell scripts, tracked JSON and secrets.

## Rust and Slint checks

The Linux job installs the pinned Rust toolchain on Ubuntu 24.04, checks formatting, runs Clippy with warnings as errors, and runs `tests/test_launcher_core.py`. CI checks out the files named by the tests at the exact Longshot commit in `tests/longshot.ref` and compares the game languages, upscaler license pins, save account, shader cache names and builder errors. Update the pin and comparisons together when the builder contract changes. It also runs the pinned Tooling release tests and verifies the signed v1.3.0 and v1.3.1 manifests with the launcher’s production public keyring. Cargo registry downloads are cached by toolchain version and `Cargo.lock`. Only successful builds on `main` populate that cache.

**The release's cache.** `release.yml`'s build keeps two, as Longshot's `build-linux.yml` does, restored and saved only by a run of `main` or a `v*` tag on the release runner. `release-downloads-<Cargo.lock, package.sh>` holds downloads only (Cargo.lock's `.crate` files, each checked against Cargo.lock's SHA-256 before the build by `scripts/ci_check_crates.py`, since cargo checks only what it downloads, and package.sh's appimagetool and runtime, checked by their SHA-256), so it can cost a download but never change what is built. Each run's entry is saved under its own run's key when it restored none for this `Cargo.lock`, or when the check removed a crate, so a repaired entry replaces a bad one. `release-rust-target-<rust-toolchain.toml, Cargo.toml>-<Cargo.lock>` holds cargo's `target` folder for both shipped programs, saved right after the compile; another `Cargo.lock` restores the newest entry for the same Rust and `Cargo.toml`. What it holds is not checked again, the weaker guarantee Longshot chose for the time (its `docs/dev/reference/ci.md`, The release's cache): cold, the two release compiles ran past 45 minutes on the release runner's 2 cores (run 1066). Only a run of `main` or a tag, on the release runner, reaches it; someone who can push a branch could still write a workflow that plants one, so before a stable you doubt, delete the `release-rust-target-` entries and it builds cold. Wine is installed in the background while they compile.

Packaging builds one Windows x64 executable and one Linux x64 AppImage with `scripts/package.sh`. The release also includes the older setup, portable ZIP and AppImage filenames for existing updaters. The Windows executables link their compiler runtime statically using Longshot’s pinned LLVM-MinGW. The Linux linker targets glibc 2.27, the v1.3.0 baseline. The packaged GUI starts on an isolated X display with synthetic settings and save bytes. The first-run tests exercise both install modes and reopen the download to check that the choice persists.

The compatibility job compares the pinned sources of v1.3.0 (`3af691ed6deedba39d3776bf7a7e80f2dbc09fca`) and v1.3.1 (`23a9e6d5625329487ba279a9b0c38b1195da527c`). The only changes in Core concern build registration and leftover folders. The job compiles the shared updater from v1.3.1 and checks that both predecessors offer each candidate asset. Its Linux and Windows processes verify fixture-signed metadata, stage the new files, run their self-checks and swap them into place. The Windows process runs under WineHQ 11.22 on Ubuntu Noble and exercises both setup and portable packages. Before rollback, the Linux job opens the updated AppImage on an isolated display and checks that the existing library is used and its synthetic save migrated. Rust rollback must restore the previous file byte-for-byte.

To verify a genuine predecessor locally, verify its signed manifest with the production release tool and check the original executable against the manifest. Set `HANDOFF_ORIGINAL_LINUX` and/or `HANDOFF_ORIGINAL_WINDOWS` to its absolute path before running the handoff. `HANDOFF_ORIGINAL_TAG` defaults to `v1.3.1` and can be set to `v1.3.0`. The harness copies each supplied binary into the temporary test directory and checks its version before the swap and again after rollback.

With Rust, Wine, Xvfb and the .NET 10 SDK for the previous updater installed, run:

```sh
rustup target add x86_64-pc-windows-gnullvm
cargo fmt --all -- --check
cargo clippy --locked --all-targets -- -D warnings
python3 -I tests/test_launcher_core.py --longshot /path/to/pinned/Longshot
scripts/package.sh v2.0.0 target/rust-candidate
tests/update_handoff/run.sh v2.0.0 target/rust-candidate target/handoff-run
```

Packaging requires an x86_64 Linux host with pkg-config, binutils, fontconfig and libudev development packages. `scripts/ci-rust-toolchain.sh` installs the CI prerequisites on Ubuntu or Debian. Packaging downloads and checks its compiler and AppImage tools. Offline builds can supply an unpacked LLVM-MinGW via `LONGSHOT_LLVM_MINGW`, Zig 0.17.0 via `ZIG`, and the pinned AppImage binaries via `APPIMAGETOOL` and `APPIMAGE_RUNTIME`.

## Launcher updates

The Rust updater selects a release on the configured channel, verifies its signed checksums and stages the package before replacing the installed launcher. The selection and installation code is in [src/update.rs](../src/update.rs), the catalog and signature checks are in [src/update/releases.rs](../src/update/releases.rs), and the trusted keys are in [assets/keys/release_keys.json](../assets/keys/release_keys.json). The [shared release guide](releases.md) covers both products.

## Release

Forgejo’s release workflow builds nightlies on `main` and stables from approved version tags. It checks both shipped programs before the publish job verifies and signs the release files. Only the restricted deploy job accesses the signing key and publishes. The launcher and Tooling share `assets/keys/release_keys.json` through `scripts/release_keys.json`. The workflow passes `--product launcher` to the pinned Tooling release script.
