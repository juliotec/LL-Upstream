# Contributing to the Longshot Launcher

The launcher installs [Longshot](https://git.magicbean.dev/MagicBeanDev/Longshot)'s builder, builds the game on the
player's PC from their own copy, starts it, keeps it and itself up to date, and moves saves. The game, its builder and
their releases live in Longshot's repository; this one holds the launcher, its tests and its own releases. It lives on the
team's Forgejo, [git.magicbean.dev/MagicBeanDev/LongshotLauncher](https://git.magicbean.dev/MagicBeanDev/LongshotLauncher).

## The rule with no exceptions

**No game files, keys, or anything made from them leave your PC**, as in Longshot (its
[CONTRIBUTING.md](https://git.magicbean.dev/MagicBeanDev/Longshot/src/branch/main/CONTRIBUTING.md)): no `.nup`, `romfs`,
`exefs`, `.nso`, `.nca`, `.nsp`, `.xci`, `.nsz`/`.xcz`, no title keys, no build of the game, and **no saves**. The save
tests use fake save folders made of text files (`tests/fixtures/hookshot-saves`, [docs/saves.md](docs/saves.md)); keep it that way. A screenshot of the
launcher is fine. Longshot's one exception, its scrubbed saves in `assets/saves/` (one slot each, picture grey, date reset,
added through a reviewed pull request), lives in Longshot's repository, never here.

## Get started

After clone, install git hooks once: `mise run setup` (pre-commit and Conventional Commit `commit-msg` hook).

```
git clone --depth 1 https://git.magicbean.dev/MagicBeanDev/LongshotLauncher.git   (jj: jj git clone --colocate --depth 1 ...)
cd LongshotLauncher
dotnet build LongshotLauncher.slnx                     (the .NET 10 SDK, Windows or Linux)
dotnet run --project App
python -I tests/test_launcher_core.py                  (the core's tests: no game, no network, no window)
python -I tests/test_release.py                        (scripts/release.py; needs the openssl command line)
```

`--depth 1` fetches the newest commit only. Pulling, branches and pull requests work as usual; `git log` past it, and cutting a
release (`scripts/release.py` counts and checks commits), need the history: `git fetch --unshallow --tags` once.

Sign in to git.magicbean.dev with the team's tooling, [MagicBeanDev/Tooling](https://git.magicbean.dev/MagicBeanDev/Tooling), cloned beside this checkout
(`git clone https://git.magicbean.dev/MagicBeanDev/Tooling.git ../Tooling`, then `python -I ../Tooling/scripts/forge.py login`: one sign-in covers every
repository).

### Tools: mise, CircleCI and AI agents

- **mise** pins every tool this repo uses. Install it ([mise docs](https://mise.jdx.dev/getting-started.html)), then in the repo run `mise install` and, once per clone, `mise run setup` (it installs the pre-commit and commit-msg hooks). `mise run lint` and `mise run test` run the same checks CI does.
- **CircleCI** runs CI for this repo; [docs/ci.md](docs/ci.md) has the flow and what runs when. Sign up at https://circleci.com/signup and ask in Discord to be added to the MagicBeanDev org, so you can see runs, read logs and rerun jobs.
- **CircleCI CLI:** `mise install` installs the pinned version, or see the [official install docs](https://circleci.com/docs/guides/toolkit/circleci-cli/). Run `circleci auth login` once.
- **CircleCI MCP server**, so an AI agent can read failures and logs and rerun jobs. Claude Code: `claude mcp add --transport http circleci-mcp-server https://mcp.circleci.com/v1/mcp`, then `/mcp` and Authenticate. Cursor: search "circleci" in the MCP marketplace.
- **CircleCI docs MCP**, so an agent can look things up in CircleCI's docs (no account needed): `claude mcp add --transport http circleci-docs https://circleci.mcp.kapa.ai`.
- **Chunk CLI** (optional) runs this repo's checks on a remote CircleCI sidecar, handy for an agent or a machine without the toolchain. Install with `brew install CircleCI-Public/circleci/chunk` (Windows: see its [README](https://github.com/CircleCI-Public/chunk-cli)), run `chunk auth login`, then `chunk sidecar create`, `chunk sidecar sync` and `chunk validate`. The commands it runs are in `.chunk/config.json`.

## Changes and tests

- **Tests read the source.** `tests/launcher_core` compiles `Core/` with no package; the screens' logic it can reach is
  checked there (`ScreenChecks.cs`). A change to the core comes with its check in the same pull request.
- **The release format is shared.** `scripts/release.py`, `cliff.toml`, `docs/releases.md` and `RELEASING.md` are the same
  files in Longshot's repository; a change to one goes to both. `tests/fixtures/release` holds a `SHA256SUMS` signed by
  `scripts/release.py` with a test key, which the launcher's tests check with the launcher's own code.
- **The key.** `Core/ReleaseTrust.cs` lists the public key the launcher trusts; Longshot's `scripts/release_keys.json` must
  list the same one. Never commit a private key (the test key in `tests/fixtures/release` is public on purpose and is
  never trusted).
- **Packages are pinned.** `Directory.Packages.props` pins each package exactly and `packages.lock.json` records what
  they bring; CI restores in locked mode. A package change updates the lock files in the same pull request.
- **Two platforms.** Windows and Linux (and the Steam Deck) from the same code; keep paths portable and UTF-8.

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
4. CI is on CircleCI ([docs/ci.md](docs/ci.md)); locally run `mise run ci:smoke` before you push. Forgejo Actions
   (`.forgejo/workflows/ci.yml`) still mirror the compile-and-test checks until maintainers retire them.

A maintainer reviews and squash-merges it. Releases are tags on `main` ([RELEASING.md](RELEASING.md)): `vX.Y.Z-rc.N`,
then `vX.Y.Z`, and every merge makes a nightly. The title decides the next version: CI refuses a tag lower than the titles
since the last stable call for (a `!` or `BREAKING CHANGE:`: the next major; a `feat`: the next minor; anything else: the
next patch), and git-cliff turns the titles into the release notes. The launcher's numbers start at v1.0.0 and are its own.

## Licensing

The launcher is free software under the [GNU General Public License, version 3 or later](LICENSE), as Longshot is. By
submitting a change you license it under those terms. The Inter font (`App/Assets/Fonts`) keeps its own licence,
`Inter-LICENSE.txt`.
