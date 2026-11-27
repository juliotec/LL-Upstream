"""Delete every cached .crate file whose SHA-256 is not Cargo.lock's, so cargo downloads it again.

Cargo checks a crate's checksum when it downloads it, not when it unpacks one already in its cache, so the release's
cached downloads (release.yml) are checked here before each build: a wrong file costs a download, never a different build.
Usage: python3 -I scripts/ci_check_crates.py Cargo.lock ~/.cargo/registry/cache
"""
import hashlib
import os
import pathlib
import sys
import tomllib

lock = tomllib.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
want = {f"{p['name']}-{p['version']}.crate": p["checksum"] for p in lock.get("package", []) if "checksum" in p}
kept = removed = 0
for path in pathlib.Path(sys.argv[2]).expanduser().glob("*/*.crate"):
    if hashlib.sha256(path.read_bytes()).hexdigest() == want.get(path.name):
        kept += 1
    else:
        path.unlink()
        removed += 1
if os.environ.get("GITHUB_OUTPUT"):
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as out:
        out.write(f"removed={removed}\n")
print(f"{kept} cached crates match Cargo.lock; {removed} removed, downloaded again")
