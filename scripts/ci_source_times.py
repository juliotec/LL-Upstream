"""Let a restored cargo target folder see the checkout's unchanged files as unchanged (release.yml).

Cargo decides that a workspace crate (the launcher, launcher-ui) is up to date by its files' times, and a fresh checkout
makes every file new, so every run would compile the interface again (about 2 minutes of 2 cores' time, the most of a
warm build). `record` writes each tracked file's SHA-256 into the target folder, before it is cached; `apply`, after a
restore, gives each file whose content is the same as recorded a time older than any build (2000-01-01), and leaves
every other file new, so cargo compiles again exactly the crates whose files changed. By content, never by commit
dates: a commit merged with an older date than the cached build must still be compiled.

  record <target dir>    tracked files' SHA-256, to <target dir>/source-hashes.json
  apply  <target dir>    unchanged files dated 2000-01-01 (and `changed=<count>` to $GITHUB_OUTPUT, in a job)

Python's standard library and git only.
"""
import hashlib
import json
import os
import pathlib
import subprocess
import sys

OLD = 946684800  # (2000-01-01T00:00:00Z)
NAME = "source-hashes.json"


def tracked() -> list[str]:
    out = subprocess.run(["git", "ls-files", "-z"], check=True, capture_output=True).stdout
    # (files only: a symlink's target is a tracked file itself, or is not the checkout's)
    paths = (pathlib.Path(p) for p in out.decode().split("\0") if p)
    return [str(p) for p in paths if p.is_file() and not p.is_symlink()]


def digest(path: str) -> str:
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


def main(argv: list[str]) -> int:
    match argv:
        case ["record", target]:
            hashes = {p: digest(p) for p in tracked()}
            pathlib.Path(target, NAME).write_text(json.dumps(hashes, sort_keys=True), encoding="utf-8")
            print(f"{len(hashes)} files' SHA-256 recorded")
        case ["apply", target]:
            try:
                recorded = json.loads(pathlib.Path(target, NAME).read_text(encoding="utf-8"))
            except (OSError, ValueError):
                print("no recorded files in the restored target folder: every file is new")
                return 0
            same = changed = 0
            for p in tracked():
                if recorded.get(p) == digest(p):
                    os.utime(p, (OLD, OLD))
                    same += 1
                else:
                    changed += 1
            print(f"{same} files unchanged since the cached build (dated 2000-01-01), {changed} new or changed")
            if os.environ.get("GITHUB_OUTPUT"):
                with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as out:
                    out.write(f"changed={changed}\n")
        case _:
            print(__doc__, file=sys.stderr)
            return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
