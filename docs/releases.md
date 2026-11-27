# Releases and updates

Longshot releases the game builder. Longshot Launcher releases the app that installs the builder, keeps game versions
and profiles, and starts the game. The two products share a release format and publish independently on Forgejo.

This guide is maintained in both repositories. Use
[Longshot's builder release guide](https://git.magicbean.dev/MagicBeanDev/Longshot/src/branch/main/docs/dev/reference/releases.md)
for builder packaging and tools, and
[the launcher release checklist](https://git.magicbean.dev/MagicBeanDev/LongshotLauncher/src/branch/main/RELEASING.md)
for launcher packaging and compatibility checks.

## Release files

| Product | Main downloads |
|---|---|
| Longshot | A self-contained `longshot-builder-<tag>-<platform>` program (`.exe` on Windows), a signed-release manifest `longshot-builder-<tag>-<platform>.json`, and any delta files named in that manifest |
| Longshot Launcher | `LongshotLauncher-<tag>-windows-x64.exe` and `LongshotLauncher-<tag>-linux-x64.AppImage`, with compatibility filenames for existing installs and download links |

A builder release packs its tools and sources. Longshot's `tools.lock` pins the archives CI uses when it makes that
pack. See [tools.lock in the builder release guide](https://git.magicbean.dev/MagicBeanDev/Longshot/src/branch/main/docs/dev/reference/releases.md#toolslock).

Each release includes `SHA256SUMS`, with one SHA-256 digest and filename per line, and `SHA256SUMS.sig`, the base64
encoding of an ECDSA P-256 signature in DER format. The signature covers the exact bytes of `SHA256SUMS`. CI verifies
its signature against the published keyring before it publishes the draft. The launcher verifies the signed checksums
and the bytes it downloads before running a program. When it applies a builder delta, it verifies the resulting
full program against the manifest too.

The launcher reads its keyring from
[`assets/keys/release_keys.json`](https://git.magicbean.dev/MagicBeanDev/LongshotLauncher/src/branch/main/assets/keys/release_keys.json).
Longshot uses
[`scripts/release_keys.json`](https://git.magicbean.dev/MagicBeanDev/Longshot/src/branch/main/scripts/release_keys.json).
Both pin Tooling's [`scripts/release.py`](https://git.magicbean.dev/MagicBeanDev/Tooling/src/branch/main/scripts/release.py)
with `.tooling-rev`. The signing key is held by the restricted release job.

## The channels

| Channel | Published release selected |
|---|---|
| Stable | The highest `vX.Y.Z` on its product's release list |
| Nightly | The highest count in `nightly-<count>-<8 lowercase hex>` on its product's release list |

Longshot starts at `v3.0.0`. Launcher releases start at `v1.0.0`. Drafts, previous release candidates and Hookshot
versions are outside these channels. Each installed game version follows its own channel. The launcher follows the
launcher channel selected in **Settings → Launcher**. Its current channel defaults to the track it was installed from.

CI publishes a release after it has uploaded all signed assets to the draft. Maintainers cut a stable by pushing a tag
on main. CI checks its version against the Conventional Commits since the prior stable. A breaking change calls for
a major bump, a feature calls for a minor bump, and a fix calls for a patch bump. Nightly runs publish when there is
something to put in the notes. Changes to an already published version go into the next release.

## How the launcher installs updates

A game version updates to a newer builder release on the same track. The launcher stages the builder and completes
the build before recording the replacement. Saves and mod choices belong to the active profile.
Older Longshot releases that use the Python builder need the previous launcher.

The launcher stages its own verified update beside the running program, runs the candidate's `--self-check`, and
restarts into the replacement. The previous program is kept as `<name>.old` for `--rollback`. On the same track it
only offers later releases. Choosing the other track can offer an older compatible launcher. Development builds
and package-managed copies have their own update mechanisms.

## Installing the launcher

The Rust launcher ships one program per platform. Download `LongshotLauncher-<v>-windows-x64.exe` on Windows or
`LongshotLauncher-<v>-linux-x64.AppImage` on Linux. On first launch, choose where the program and its data should live.

| Choice | Where files go |
| --- | --- |
| Install on Windows | The program goes in `%LOCALAPPDATA%\Programs\LongshotLauncher`, with a Start menu shortcut and an Apps & features entry. |
| Install on Linux | The AppImage goes in `$XDG_DATA_HOME/longshot`, normally `~/.local/share/longshot/LongshotLauncher.AppImage`. The installer adds an applications menu entry. |
| Use the download portably | A `Longshot` folder beside the program contains `.portable`, `data/` for profiles, saves, mods and versions, and `cache/` for generated build files. Move the program and this folder together. |

An existing per-user installation reuses its standard data home. Opening a newer download on the same track can
upgrade the installed copy and launch it. Existing portable installations reuse their data location. Windows recognizes the `portable` file beside the executable
and its `LongshotLauncher-data` folder. Linux recognizes an existing `<AppImage>.home` folder. Package-managed copies
receive launcher updates through their package manager.

**Compatibility downloads.** Releases also publish the Windows executable under its old `-windows-x64-setup.exe`
name, a `-windows-x64-portable.zip` containing that same executable, and the AppImage under its old `-x86_64.AppImage`
name. Older launchers and permanent download links can continue using these assets. Extracting the ZIP into a fresh
folder opens the same install choice.

**Switching tracks.** Choose Stable or Nightly in Settings → Launcher to check that channel. When a compatible version
is available, choose **Install and restart**. An installed copy or a legacy portable copy can switch to an older release
using its original asset name. A new portable copy checks whether the target release understands its data layout. When a
stable release predates that layout, Settings explains the limitation and directs the player to an installed copy.

`LONGSHOT_LAUNCHER_TRUST=<public key .pem>` trusts one more key, to try a release signed with a test key.

## Release notes

git-cliff writes them at release time from the commit titles since the previous tag, with the repository's `cliff.toml`.
For a stable, that is since the previous stable. A nightly after the first has two lists: its own, headed
`## <tag> (<date>), new since <nightly>`, the commits since the nightly before; then `## Earlier changes (since <stable>)`,
what came since the last stable in one list (`release.py notes`). The headings are Features,
Fixes, Performance, Reverted, Documentation, Code, Build and Tests; `ci` and `chore` are left out. The notes go on the
release page only: nothing is committed back to `main`, and there is no changelog file to keep up (Hookshot's is in
NotRexed/Hookshot).

`release.py publish` puts a header above the notes (`release_page`), so a player who lands on a release page downloads the
right thing:

- **The launcher's releases** start with big download links to that release's own files: *Download for Windows* (the
  launcher), *Download for Linux* (the AppImage), and a small link to the portable zip, all through git-cdn.magicbean.dev
  (the same paths as git.magicbean.dev, from Cloudflare's edge).
- **Longshot's releases** start with "To play, get the Longshot Launcher": the same big links, to the newest launcher of
  the **same channel** (a nightly to the launcher's nightly, a stable to its stable) through the links
  below, then "Don't download the files below": they are the builders the launcher installs.

## Download links

The Longshot website provides permanent links to the newest launcher of each channel:

| Link | Goes to |
|---|---|
| `https://longshot.magicbean.dev/download` | a page: the launcher for the visitor's system, from the best channel there is, and every channel |
| `https://longshot.magicbean.dev/download/launcher/<os>` | that file of the best channel there is: stable, else nightly |
| `https://longshot.magicbean.dev/download/launcher/<channel>` | the channel's newest release page |
| `https://longshot.magicbean.dev/download/launcher/<channel>/<os>` | that file of the channel's newest release |

`<channel>` is `stable` or `nightly`; `<os>` is `windows` (the executable), `windows-portable` (the zip) or `linux` (the
AppImage). A channel with no release yet answers with a page that says so and offers the newest there is. The READMEs'
download buttons and Longshot's release pages use these links; there are none for Longshot's builder files, which only
the launcher downloads.
