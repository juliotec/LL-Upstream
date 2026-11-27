"""Run on an empty X display with xvfb-run -a python -I tests/test_rust_window.py PATH.

A release AppImage also exercises the interactive install and portable choices.
"""

from contextlib import contextmanager
import filecmp
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time


def isolated_environment(directory):
    inherited = {key: value for key, value in os.environ.items()
                 if not key.startswith(("LONGSHOT_", "HOOKSHOT_", "HOS_", "APPIMAGE", "XDG_"))
                 and key not in ("APPDIR", "TARGET_APPIMAGE", "WAYLAND_DISPLAY", "FLATPAK_ID", "SNAP")}
    runtime = directory / "runtime"
    runtime.mkdir(mode=0o700)
    return dict(inherited, HOME=str(directory), XDG_DATA_HOME=str(directory / "data"),
                XDG_CONFIG_HOME=str(directory / "config"), XDG_CACHE_HOME=str(directory / "cache"),
                XDG_STATE_HOME=str(directory / "state"), XDG_RUNTIME_DIR=str(runtime),
                SLINT_BACKEND="winit-software", SLINT_SCALE_FACTOR="1", LC_ALL="C",
                LONGSHOT_LAUNCHER_NO_PREFETCH="1", APPIMAGE_EXTRACT_AND_RUN="1")


def visible_windows(env):
    result = subprocess.run(["xdotool", "search", "--onlyvisible", "--name", "^Longshot Launcher$"],
                            env=env, capture_output=True, text=True, check=False)
    if result.returncode not in (0, 1):
        raise RuntimeError(result.stderr)
    return result.stdout.splitlines()


def x(env, *args):
    return subprocess.check_output(["xdotool", *args], env=env, text=True, timeout=10)


def window_size(env, window):
    geometry = dict(line.split("=", 1) for line in
                    x(env, "getwindowgeometry", "--shell", window).splitlines())
    return int(geometry["WIDTH"]), int(geometry["HEIGHT"])


def wait_until(fn, description, timeout=30):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if result := fn():
            return result
        time.sleep(0.1)
    raise RuntimeError(f"Timed out waiting for {description}")


@contextmanager
def launch_download(executable, env, directory):
    # Installing hands control to a child. Keep both processes inside the fixture.
    process = subprocess.Popen([executable], env=env, cwd=directory, start_new_session=True)
    try:
        yield process
    finally:
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        process.wait(timeout=15)


def exercise_install_choice(executable, directory, *, portable):
    env = isolated_environment(directory)
    downloads = directory / "Downloads"
    downloads.mkdir()
    source = downloads / executable.name
    shutil.copy2(executable, source)
    portable_root = downloads / "Longshot"
    standard_data = directory / "data/longshot"
    installed = standard_data / "LongshotLauncher.AppImage"
    menu = directory / "data/applications/longshot-launcher.desktop"
    profiles = (portable_root / "data" if portable else standard_data) / "profiles"

    def window_of_size(chooser):
        for window in visible_windows(env):
            try:
                width, _ = window_size(env, window)
            except subprocess.CalledProcessError:
                # The chooser can unmap between the two X11 calls.
                if window not in visible_windows(env):
                    continue
                raise
            if (width < 960) == chooser:
                return window
        return None

    def close_main(program):
        window = wait_until(lambda: window_of_size(False), "main launcher window")
        pid = x(env, "getwindowpid", window).strip()
        environment = Path(f"/proc/{pid}/environ").read_bytes().split(b"\0")
        assert os.fsencode(f"APPIMAGE={program}") in environment, "Wrong AppImage is running"
        wait_until(lambda: (profiles / "default/profile.json").is_file(), "default profile")
        width, _ = window_size(env, window)
        x(env, "windowmove", window, "0", "0")
        x(env, "mousemove", "--window", window, str(width - 32), "32", "click", "1")
        wait_until(lambda: not visible_windows(env), "launcher to close")

    with launch_download(source, env, directory) as app:
        window = wait_until(lambda: window_of_size(True), "first-launch chooser")
        x(env, "windowfocus", "--sync", window)
        x(env, "key", "Escape")
        assert app.wait(timeout=10) == 0
    assert not any(path.exists() for path in (portable_root, installed, menu, profiles))

    with launch_download(source, env, directory):
        window = wait_until(lambda: window_of_size(True), "chooser after dismissal")
        width, height = window_size(env, window)
        x(env, "windowmove", window, "0", "0")
        x(env, "mousemove", "--window", window, str(width - (155 if portable else 53)),
          str(height - 45), "click", "1")
        close_main(source if portable else installed)

    if portable:
        assert (portable_root / ".portable").is_file()
        assert (portable_root / "cache").is_dir()
        assert not any(path.exists() for path in (installed, menu, standard_data / "profiles"))
    else:
        assert filecmp.cmp(source, installed, shallow=False)
        assert str(installed) in menu.read_text()
        assert not portable_root.exists()

    if not portable:
        # Treat a signed older installation as a distinct file so startup of
        # the existing copy cannot accidentally satisfy this assertion.
        installed.write_text("#!/bin/sh\nexit 94\n")
        installed.with_name(installed.name + ".version").write_text("v1.3.1\n")
        with launch_download(source, env, directory):
            close_main(installed)
        assert filecmp.cmp(source, installed, shallow=False), "Opening the newer download did not upgrade the installation."

    # A second start must reuse the chosen profile and bypass the installer.
    # Starting the original download after installation must run the installed copy.
    profile = profiles / "default/profile.json"
    before = json.loads(profile.read_text())
    before["name"] = "Remembered test player"
    profile.write_text(json.dumps(before))
    for program in ([source] if portable else [source, installed]):
        with launch_download(program, env, directory):
            close_main(source if portable else installed)
        assert json.loads(profile.read_text()) == before


def check_published_upgrade(stable, candidate, directory):
    """Exercise first start with preferences actually written by the 1.3.1 AppImage."""
    name = "LongshotLauncher-v1.3.1-x86_64.AppImage"
    sums = Path(__file__).parent / "fixtures/release/v1.3.1/SHA256SUMS"
    expected = next(row.split()[0] for row in sums.read_text().splitlines() if row.split()[-1] == name)
    with stable.open("rb") as file:
        assert hashlib.file_digest(file, "sha256").hexdigest() == expected, "Expected the published 1.3.1 AppImage"
    env = isolated_environment(directory)
    data = directory / "data/longshot"
    data.mkdir(parents=True)
    installed = data / "LongshotLauncher.AppImage"
    shutil.copyfile(stable, installed)
    installed.chmod(0o755)
    with launch_download(installed, env, directory):
        wait_until(lambda: visible_windows(env), "published stable window")
    # The AppImage runtime exits before Avalonia finishes graceful shutdown.
    # The stable launcher's instance lock must be released before handoff.
    import fcntl
    def stable_closed():
        with (data / "launcher/launcher.lock").open("rb") as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                return False
            return not visible_windows(env)
    wait_until(stable_closed, "stable launcher shutdown")
    settings = data / "launcher/settings.json"
    previous_settings = settings.read_bytes()
    slot = Path("save/5a3c9e1f0b7d24680000000000000001/slot_00/progress.sav")
    original = data / "user" / slot
    original.parent.mkdir(parents=True, exist_ok=True)
    original.write_bytes(b"synthetic stable save")
    installed.rename(installed.with_suffix(".AppImage.old"))
    shutil.copyfile(candidate, installed)
    installed.chmod(0o755)
    with launch_download(installed, dict(env, APPIMAGE=str(installed)), directory):
        window = wait_until(lambda: next(iter(visible_windows(env)), None), "new launcher window")
        assert window_size(env, window)[0] >= 960, "The in-place upgrade reopened the install chooser"
        migrated = data / "profiles/default/saves" / slot
        wait_until(migrated.is_file, "copied stable save")
        assert migrated.read_bytes() == original.read_bytes()
        assert settings.read_bytes() == previous_settings
        assert not (installed.parent / "Longshot").exists()


def check_handoff(executable, directory):
    """The updater keeps the AppImage filename and puts the original beside it."""
    env = isolated_environment(directory)
    data = directory / "data/longshot"
    control = data / "launcher"
    control.mkdir(parents=True)
    previous = data / "user/save/5a3c9e1f0b7d24680000000000000001/slot_00/progress.sav"
    previous.parent.mkdir(parents=True)
    previous.write_bytes(b"synthetic stable save")
    old_settings = {"format": 2, "checkUpdates": False, "relistedLostBuilds": True,
                    "keptFolders": [], "keptFolderNames": []}
    (control / "settings.json").write_text(json.dumps(old_settings))
    fixture_version = directory / "Games/Longshot/stable"
    fixture_version.mkdir(parents=True)
    (control / "builds.json").write_text(json.dumps({
        "format": 1, "home": str(control),
        "builds": [{"id": "stable", "folder": str(fixture_version), "version": "v3.2.0",
                    "channel": "stable", "default": True}],
    }))
    with launch_download(executable, env, directory):
        window = wait_until(lambda: next(iter(visible_windows(env)), None), "upgraded launcher window")
        assert window_size(env, window)[0] >= 960, "An in-place upgrade reopened the install chooser."
        saved = data / "profiles/default/saves" / previous.relative_to(data / "user")
        wait_until(saved.is_file, "migrated stable save")
        assert saved.read_bytes() == previous.read_bytes()
        assert not (executable.parent / "Longshot").exists(), "The upgrade opened a new portable library."


def exercise(executable, directory, *, upgrading=False):
    root = directory / "launcher"
    root.mkdir()
    settings = root / "launcher-settings.json"
    prior = root / "settings.json"
    initial_settings = {"format": 2,
                       "defaultChannel": "stable", "checkUpdates": False, "fixture": 319}
    if upgrading:
        initial_settings.update(relistedLostBuilds=True, keptFolders=[], keptFolderNames=[])
        prior.write_text(json.dumps(initial_settings))
    else:
        settings.write_text(json.dumps(initial_settings))
    slot = Path("save/5a3c9e1f0b7d24680000000000000001/slot_00/progress.sav")
    previous = directory / "data/longshot/user" / slot
    previous.parent.mkdir(parents=True)
    previous.write_bytes(b"synthetic untouched external file")
    if upgrading:
        game = directory / "Games/Longshot/stable"
        mod = game / "mods/Forest/romfs/fixture.bin"
        mod.parent.mkdir(parents=True)
        mod.write_bytes(b"synthetic stable mod")
        entry = {"id": "stable-fixture", "name": "Longshot", "folder": str(game),
                 "version": "v3.2.0", "channel": "stable", "default": True}
        registry = {"format": 1, "home": str(root), "builds": [entry]}
        (root / "builds.json").write_text(json.dumps(registry))
    env = isolated_environment(directory)
    env["LONGSHOT_LAUNCHER_HOME"] = str(root)
    downloads = Path(env["HOME"]) / "Downloads"
    downloads.mkdir(exist_ok=True)
    with open(os.fsencode(downloads) + b"/game-\xff.nup", "wb"):
        pass
    headless = env.copy()
    headless.pop("DISPLAY", None)
    check = subprocess.run([executable, "--self-check"], capture_output=True, text=True,
                           env=headless, check=True, timeout=30)
    assert check.stdout.strip().startswith("LongshotLauncher "), check.stdout

    with subprocess.Popen([executable], env=env, cwd=directory) as app:
        try:
            deadline = time.monotonic() + 30
            while not (visible := visible_windows(env)):
                if app.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError(f"Window did not appear. Process status {app.poll()}")
                time.sleep(0.1)
            assert len(visible) == 1, visible
            window = visible[0]
            subprocess.run([executable], env=env, cwd=directory, check=True, timeout=30)
            # Wait until the first instance has processed the second one's focus request.
            deadline = time.monotonic() + 15
            while (root / "launcher.show").exists():
                if time.monotonic() >= deadline:
                    raise RuntimeError("The first launcher did not handle the second launch")
                time.sleep(0.05)
            assert visible_windows(env) == [window], "A second launch must reuse the open window"
            x(env, "windowmove", window, "0", "0")
            x(env, "windowfocus", "--sync", window)
            x(env, "key", "Escape")
            deadline = time.monotonic() + 5
            while not json.loads(settings.read_text()).get("setupVisited"):
                if time.monotonic() >= deadline:
                    raise RuntimeError("Leaving first-run setup did not return to the launcher")
                time.sleep(0.05)
            assert (root / "profiles/default").is_dir()
            assert json.loads(settings.read_text()).get("fixture") == (None if upgrading else 319)
            assert previous.read_bytes() == b"synthetic untouched external file"
            migrated = root / "profiles/default/saves" / slot
            if upgrading:
                assert json.loads(prior.read_text()) == initial_settings
                assert migrated.read_bytes() == previous.read_bytes()
                copies = list((root / "profiles/default/mods").rglob("fixture.bin"))
                assert len(copies) == 1 and copies[0].read_bytes() == b"synthetic stable mod"
                assert mod.read_bytes() == b"synthetic stable mod"
            else:
                assert not prior.exists()
                assert not migrated.exists()

            # The second process may have a different working directory.
            for name in ("first", "second"):
                installation = root / "versions" / name
                (installation / "builds/default").mkdir(parents=True)
                (installation / "Longshot.x86_64").touch()
                subprocess.run([executable, "--launch", str(installation.relative_to(directory))],
                               env=env, cwd=directory, check=True, timeout=30)
                profile = root / "profiles/default/profile.json"
                deadline = time.monotonic() + 10
                while json.loads(profile.read_text()).get("version") != name:
                    if time.monotonic() >= deadline:
                        raise RuntimeError(f"The existing window did not select {name}")
                    time.sleep(0.05)
                assert visible_windows(env) == [window]

            width, height = window_size(env, window)
            assert width >= 960 and height >= 560, (width, height)
            display_width, _ = map(int, x(env, "getdisplaygeometry").split())
            if display_width < width:
                # A small Xvfb screen can be narrower than the minimum window.
                # Bring its title-bar controls onto the display before clicking.
                x(env, "windowmove", "--sync", window, str(display_width - width), "0")
            x(env, "mousemove", "--window", window, str(width - 32), "32", "click", "1")
            assert app.wait(timeout=15) == 0
        finally:
            if app.poll() is None:
                app.kill()


if __name__ == "__main__":
    if sys.argv[1] == "--published-upgrade":
        with tempfile.TemporaryDirectory(prefix="longshot-published-upgrade-") as temporary:
            check_published_upgrade(Path(sys.argv[2]), Path(sys.argv[3]), Path(temporary))
        print("Verified upgrade of a home created by the published v1.3.1 launcher.")
        sys.exit(0)
    if sys.argv[1] == "--check-handoff":
        with tempfile.TemporaryDirectory(prefix="longshot-first-updated-start-") as temporary:
            check_handoff(Path(sys.argv[2]).resolve(strict=True), Path(temporary))
        print("Verified first start after the .NET updater swap opens the existing library.")
        sys.exit(0)
    executable = Path(sys.argv[1]).resolve(strict=True)
    if executable.suffix == ".AppImage":
        for portable in (True, False):
            with tempfile.TemporaryDirectory(prefix="longshot-install-choice-") as temporary:
                exercise_install_choice(executable, Path(temporary), portable=portable)
        print("Verified both first-launch choices, dismissal, data locations and relaunching each copy.")
        with tempfile.TemporaryDirectory(prefix="longshot-existing-home-") as temporary:
            directory = Path(temporary)
            download = directory / executable.name
            shutil.copy2(executable, download)
            assert not download.with_name(download.name + ".old").exists()
            check_handoff(download, directory)
        print("Verified an existing standard home opens without the updater backup.")
    with tempfile.TemporaryDirectory(prefix="longshot-ui-") as temporary:
        exercise(executable, Path(temporary))
    with tempfile.TemporaryDirectory(prefix="longshot-stable-upgrade-") as temporary:
        exercise(executable, Path(temporary), upgrading=True)
    print("Verified stable mods and saves migrate on upgrade and external saves stay untouched on a fresh install.")
    print("Passed headless self-check, native window, single instance, first-run navigation, profile initialization, untouched external data and close.")
