# Notes for AI coding agents

Follow [CONTRIBUTING.md](CONTRIBUTING.md): its rules apply to agents as to everyone, above all that no game files, saves or
anything made from them leave the PC. [README.md](README.md) has the layout and the commands, [docs/releases.md](docs/releases.md)
how releases are made and checked, and [docs/saves.md](docs/saves.md) the saves, including local Hookshot save imports.

What agents trip on:

- **Native launcher.** Rust application code lives in `src/`, Slint views in `ui/`, and local artwork and catalogs in `assets/`. Use the current Longshot builder protocol. Only Hookshot saves are imported. Keep its launcher settings, channels, versions and caches separate. Do not add compatibility with Hookshot 2.x builders.
- **Test before opening a PR.** Run `python -I tests/test_launcher_core.py` and `LAUNCHER=$PWD python -I ../Tooling/tests/test_release.py`. Run the locked Clippy and formatting checks in `mise.toml`. Update a check in the same pull request rather than weakening it.
- **Two repositories, one release format.** Tooling owns `scripts/release.py` and is pinned by `.tooling-rev`. The shared specification in `docs/releases.md` is also kept in Longshot. Launcher release commands select `--product launcher`. Rust and Tooling both read the public keyring in `assets/keys/release_keys.json`.
- **Never make or commit a real signing key**, and never publish a release by hand: tags and the release workflow do it
  ([RELEASING.md](RELEASING.md)). Cutting one: Tooling's release skill (`/magicbean:release`, or
  `../Tooling/skills/release/SKILL.md`).
- **jj or git**: `main` is protected; push a branch to your fork and open a pull request. Never push to
  MagicBeanDev/LongshotLauncher directly.
- **The team's tooling is [MagicBeanDev/Tooling](https://git.magicbean.dev/MagicBeanDev/Tooling)**: the sign-in, `forge.py` (forks, pull requests, issues, for
  this repository too), the team chat and the shared skills. Read its [AGENTS.md](https://git.magicbean.dev/MagicBeanDev/Tooling/src/branch/main/AGENTS.md) first. Clone it beside
  this checkout once (`git clone https://git.magicbean.dev/MagicBeanDev/Tooling.git ../Tooling`), pull it each session (`git -C ../Tooling pull --ff-only`), and
  run it from here: `python -I ../Tooling/scripts/forge.py login` signs in once for git on every repository, then
  `forge.py pr create <branch> --title "..."` opens a pull request here ([docs/forge.md](https://git.magicbean.dev/MagicBeanDev/Tooling/src/branch/main/docs/forge.md)).
- **Claude Code: check the team's plugin is installed, and suggest it if not.** `claude plugin list` shows
  `magicbean@magicbean` when it is. When it doesn't, tell the person you work for at the start of the session and give them
  the two commands to type (`/plugin marketplace add https://git.magicbean.dev/MagicBeanDev/Tooling.git`, then
  `/plugin install magicbean@magicbean`); plugin commands are theirs to run, not yours.

Pull requests: **open the description with why the PR is needed**, in one or two plain sentences, and include
screenshots when the change shows on screen.
