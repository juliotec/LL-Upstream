# Contributing to the Longshot Launcher

The launcher installs [Longshot](https://git.magicbean.dev/MagicBeanDev/Longshot)'s builder, builds the game on the
player's PC from their own copy, starts it, keeps it and itself up to date, and moves saves. The game, its builder and
their releases live in Longshot's repository; this one holds the launcher, its tests and its own releases. It lives on the
team's Forgejo, [git.magicbean.dev/MagicBeanDev/LongshotLauncher](https://git.magicbean.dev/MagicBeanDev/LongshotLauncher).

## The rule with no exceptions

**No game files, keys, or anything made from them leave your PC**, as in Longshot (its
[CONTRIBUTING.md](https://git.magicbean.dev/MagicBeanDev/Longshot/src/branch/main/CONTRIBUTING.md)): no `.nup`, `romfs`,
`exefs`, `.nso`, `.nca`, `.nsp`, `.xci`, `.nsz`/`.xcz`, no title keys, no build of the game, and **no saves**. The Rust save tests create synthetic temporary directories. **No NFC tag dump** either: the NFC tests make fake tags in code
([docs/nfc.md](docs/nfc.md)). A screenshot of the launcher is fine. Longshot's one exception, its scrubbed saves in `assets/saves/` (one slot each, picture grey, date reset,
added through a reviewed pull request), lives in Longshot's repository, never here.

## Get started

After clone, install git hooks once: `mise run setup` (pre-commit and Conventional Commit `commit-msg` hook).

```
git clone --depth 1 https://git.magicbean.dev/MagicBeanDev/LongshotLauncher.git   (jj: jj git clone --colocate --depth 1 ...)
cd LongshotLauncher
cargo build --locked
cargo run --locked
python -I tests/test_launcher_core.py                  (Rust tests and translation catalogs, using game-free fixtures)
LAUNCHER=$PWD python -I ../Tooling/tests/test_release.py   (Tooling's release.py; needs the openssl command line)
```

`--depth 1` fetches the newest commit only. Pulling, branches and pull requests work as usual; `git log` past it, and cutting a
release (Tooling's `release.py` counts and checks commits), need the history: `git fetch --unshallow --tags` once.

Sign in to git.magicbean.dev with the team's tooling, [MagicBeanDev/Tooling](https://git.magicbean.dev/MagicBeanDev/Tooling), cloned beside this checkout
(`git clone https://git.magicbean.dev/MagicBeanDev/Tooling.git ../Tooling`, then `python -I ../Tooling/scripts/forge.py login`: one sign-in covers every
repository).

**Using Claude Code?** Install the team's plugin once per PC, for the skills (`/magicbean:team-updates`,
`/magicbean:release`, `/magicbean:batch-review`) in every MagicBeanDev repository:

```
/plugin marketplace add https://git.magicbean.dev/MagicBeanDev/Tooling.git
/plugin install magicbean@magicbean
```

### Local development tools

- **mise** pins every tool this repo uses. Install it ([mise docs](https://mise.jdx.dev/getting-started.html)), then in the repo run `mise install` and, once per clone, `mise run setup` (it installs the pre-commit and commit-msg hooks). `mise run lint` and `mise run test` run the same checks CI does.

## Changes and tests

Compile the UI with the version of Slint in `Cargo.lock`. Keep filesystem, subprocess and network operations in Rust. The Slint views present models and invoke callbacks. Use synthetic fixtures and temporary launcher homes to test changes.

Run `cargo fmt --all -- --check`, `cargo clippy --locked --all-targets -- -D warnings`, `python -I tests/test_launcher_core.py`, and the Tooling release suite before opening a PR. To exercise the Linux UI on a clean display, build the executable and run `xvfb-run -a python -I tests/test_rust_window.py target/debug/LongshotLauncher`. See [CI](docs/ci.md) for release packaging and the previous-stable handoff.

## Commits and pull requests

`main` takes reviewed pull requests only: push a branch to your fork and open the pull request from there
(`jj git push --remote forge-fork -b <branch>`, then the pull request on git.magicbean.dev, or
`python -I ../Tooling/scripts/forge.py pr create <branch> --title "..."`; Tooling's [docs/forge.md](https://git.magicbean.dev/MagicBeanDev/Tooling/src/branch/main/docs/forge.md)
has the set-up).

1. One topic per pull request, on a branch named for it (`fix/...`, `feat/...`, `docs/...`).
2. Each commit subject is a [Conventional Commit](https://www.conventionalcommits.org/) (`feat(saves): ...`, `fix: ...`);
   one concern per commit when you can. The pull request title uses the same form and becomes the squash-merged commit on
   `main` and a line of the next release's notes.
3. Start the description with why the pull request is needed, in one or two plain sentences, then what changed and how it
   was tested. Include screenshots when the change shows on screen.
4. CI runs on Forgejo Actions (`.forgejo/workflows/`: lint, compile and test, and the packaging check when it is needed).
   See [docs/ci.md](docs/ci.md) for the workflow. Locally run `mise run ci:smoke` before you push.

A maintainer reviews and squash-merges it. Releases are tags on `main` ([RELEASING.md](RELEASING.md)): `vX.Y.Z`, on
a tested nightly's commit, and every merge makes a nightly. The title decides the next version: CI refuses a tag lower than the titles
since the last stable call for (a `!` or `BREAKING CHANGE:`: the next major; a `feat`: the next minor; anything else: the
next patch), and git-cliff turns the titles into the release notes. The launcher's numbers start at v1.0.0 and are its own.

## Licensing

The launcher is free software under the [GNU General Public License, version 3 or later](LICENSE), as Longshot is. By
submitting a change you license it under those terms. Inter and Libertinus carry their own licences in `assets/fonts/`.
