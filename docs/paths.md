# Where Longshot keeps files

These are the default application paths. Linux uses absolute `XDG_DATA_HOME` and `XDG_CACHE_HOME` values when set. A folder chosen in Settings can override the data or cache location. File numbers, version IDs, profile IDs and names depend on what you install or play.

## Install, data and cache roots

| Location | Linux and Steam Deck | Windows | New portable install |
| --- | --- | --- | --- |
| Launcher executable | `~/.local/share/longshot/LongshotLauncher.AppImage` | `%LOCALAPPDATA%\Programs\LongshotLauncher\LongshotLauncher.exe` | The downloaded executable |
| Launcher settings, logs and registries | `~/.local/share/longshot/launcher/` | `%LOCALAPPDATA%\LongshotLauncher\` | `Longshot/data/` beside the executable |
| Profiles, versions and shared mods | `~/.local/share/longshot/` | `%LOCALAPPDATA%\Longshot\data\` | `Longshot/data/` |
| Cache selected in Settings | `~/.cache/longshot/` | `%LOCALAPPDATA%\Longshot\cache\` | `Longshot/cache/` |
| Builder's files under that cache | `longshot-builder/` | `longshot-builder\` | `longshot-builder/` |
| NFC tags | `~/.local/share/longshot/nfc/` | `%LOCALAPPDATA%\Longshot\nfc\` | `Longshot/data/nfc/` |
| Save backups | `~/.local/share/longshot/save-backups/` | `%LOCALAPPDATA%\Longshot\save-backups\` | `Longshot/data/save-backups/` |
| Desktop integration | `$XDG_DATA_HOME/applications/longshot-launcher.desktop` and `icons/hicolor/512x512/apps/longshot-launcher.png` | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\` and the current user's LongshotLauncher uninstall entry | `Longshot/.portable` remembers this choice |

On Linux, an in-place update keeps the AppImage filename. The updater writes `.new` while staging and keeps `.old` for rollback, with an `.update-lock` during a swap. An installer also records the installed tag in `.version`.

## Launcher settings and player data

| Under the launcher settings root | Purpose |
| --- | --- |
| `launcher-settings.json` | Rust launcher preferences, including custom paths and the launcher update channel |
| `versions.json`, `version-locations/` | Current instances and ownership records for installations outside the default library |
| `settings.json`, `builds.json` | Current stable launcher's files, read for save and mod import and cleanup |
| `stable-saves-v1.json`, `stable-mods-v1.json` | Record which previous saves and mods were imported |
| `launcher.lock` | Single-instance lock on Linux |
| `logs/` | Launcher and shortcut sessions, plus the latest build and launch logs |

| Under the data root | Purpose |
| --- | --- |
| `profiles/<id>/profile.json` | Profile name, selected version and save, and profile mod settings |
| `profiles/<id>/saves/save/<account>/slot_XX/` | Save progress and optional caption or screenshot data |
| `profiles/<id>/saves/save-work/` | Game save transactions |
| `profiles/<id>/mods/`, `mods.json`, `trust.json` | Profile mods, switches and content approvals |
| `profiles/<id>/settings.ini` | Game options |
| `mods/` | Shared mod files |
| `builder-data/` | Persistent builder metadata |
| `versions/<instance>/` | The verified builder (`Longshot.exe` or `Longshot.x86_64`), `longshot_release.json`, and instance metadata |
| `versions/<instance>/builds/<build>/` | The generated game executable, renderer, resources, build metadata and game logs. A build can also have local `cache/` and `mods/` folders |

## Builder cache

These folders are relative to the builder cache root. A standalone builder uses the platform defaults above, with `--cache` or `LONGSHOT_CACHE` available for an explicit root. `--work` can name a per-build directory. `--store` or `LONGSHOT_STORE` can name the shared store.

| Directory | Purpose |
| --- | --- |
| `downloads/<tag>/` | Prefetched builder executable, release receipt and download lock |
| `builds/<instance-key>/` | Build cache for new work. Holds the build lock, generated `exefs/`, `image/`, `gen/`, `build/`, timing data, build log and game-folder reference |
| `work/<instance-key>/` | Earlier build cache. A builder can reuse an existing instance here, including with older builder releases |
| `store/compile/`, `store/lifts/` | Objects and translations shared across builds |
| `shader-store/` | Shared shader packs. `LONGSHOT_SHADER_STORE` can override the location |
| `tools/`, `sources/`, `runtimes/` | Tools, unpacked sources and optional vendor upscaler files |
| `mods/` | The builder's mod store. Where the launcher manages it, files live under the data root |

## Existing locations and imported files

- A previous Linux install can still use `~/.cache/longshot/launcher/work/`, including `longshot-builder/` below it. Earlier standalone builders can use `~/.cache/longshot-builder/`. On Windows, older caches include `%LOCALAPPDATA%\LongshotLauncher\work\` and `%LOCALAPPDATA%\Longshot\Builder\`. Storage also checks retained custom cache locations.
- Windows portable releases can use a sibling `portable` marker and `LongshotLauncher-data/`. Linux portable releases can use an adjacent `<AppImage>.home/`. Those layouts continue to resolve their existing data.
- Stable saves and mods can be read under `~/.local/share/longshot/user/` on Linux and `%APPDATA%\Longshot\` on Windows, and from older registered game folders. Save import also looks in the installed Hookshot, NX2navi, Lichii, Eden and Eglantine directories.
- The builder reads the selected `.nup`, `.nx`, or extracted `romfs/` and `exefs/` input at the location you choose. Save archives and manually selected files use their chosen paths.
- Steam integration reads or updates the selected Steam account's `config/shortcuts.vdf` and keeps its Longshot shortcut metadata beside it.
