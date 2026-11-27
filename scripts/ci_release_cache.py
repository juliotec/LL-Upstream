"""The release build's compiled Rust, cached only when signed (release.yml; docs/ci.md, The release's cache).

The build job holds no secret: after its compile it packs cargo's target folder into one archive (`pack`), caches it
under a key that ends in the run's id and gives the archive's SHA-256 to the `cache-sign` job, which signs the entry's
message with the release cache key (Ed25519, sealed; opened with seal.key from the seal broker, for main and v* tags
only) and caches the signature. That job runs no code of the checkout or of any crate. A later build restores the
newest signature, then the archive it names, and unpacks it only when the signature is the committed public key's over
that archive's key and SHA-256 (`check`): an entry anyone else saved costs a cold build, never a different one.

  pack    <target dir> <archive>                       the archive, and its SHA-256 on stdout
  check   <public key> <signature dir> <archive> <prefix> <target dir>
                                                       unpacks the archive into the target dir when its signature holds
  message <key> <sha256>                               what is signed, on stdout (cache-sign signs exactly this)
  entry   <signature dir> <prefix>                     the archive key the restored signature names, on stdout (or none)

Python's standard library and openssl only (the runners' own image).
"""
import hashlib
import pathlib
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

KEY_FORM = re.compile(r"release-rust-target-[0-9a-f]{64}-[0-9a-f]{64}-[0-9]+")


def message(key: str, sha256: str) -> bytes:
    if not KEY_FORM.fullmatch(key) or not re.fullmatch(r"[0-9a-f]{64}", sha256):
        raise SystemExit(f"not a release cache entry: {key!r} {sha256!r}")
    return f"longshot-launcher release-rust-target v1\n{key}\n{sha256}\n".encode()


def digest(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def pack(target: pathlib.Path, archive: pathlib.Path) -> None:
    # (uncompressed: actions/cache compresses the file itself)
    archive.parent.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive, "w") as tar:
        tar.add(target, arcname="target")
    print(digest(archive))


def entry(sigdir: pathlib.Path, prefix: str) -> str:
    try:
        key = (sigdir / "entry").read_text(encoding="ascii").strip()
    except (OSError, UnicodeDecodeError):
        return ""
    return key if KEY_FORM.fullmatch(key) and key.startswith(prefix) else ""


def check(pubkey: pathlib.Path, sigdir: pathlib.Path, archive: pathlib.Path, prefix: str, target: pathlib.Path) -> bool:
    key = entry(sigdir, prefix)
    if not key:
        print("no signed entry for this Rust and Cargo.toml: a cold build")
        return False
    if not archive.is_file() or not (sigdir / "sig").is_file():
        print(f"{key}: its archive or signature was not restored: a cold build")
        return False
    with tempfile.TemporaryDirectory() as tmp:
        msg = pathlib.Path(tmp, "msg")
        msg.write_bytes(message(key, digest(archive)))
        ok = subprocess.run(
            ["openssl", "pkeyutl", "-verify", "-pubin", "-inkey", str(pubkey), "-rawin",
             "-in", str(msg), "-sigfile", str(sigdir / "sig")],
            capture_output=True).returncode == 0
    if not ok:
        print(f"::warning::{key}: its signature is not the release cache key's: a cold build")
        return False
    shutil.rmtree(target, ignore_errors=True)
    with tarfile.open(archive) as tar:
        # (the "data" filter: nothing outside the folder, no links out of it, no devices)
        tar.extractall(target.parent, filter="data")
    print(f"{key}: signed, unpacked into {target}")
    return True


def main(argv: list[str]) -> int:
    match argv:
        case ["pack", target, archive]:
            pack(pathlib.Path(target), pathlib.Path(archive))
        case ["check", pubkey, sigdir, archive, prefix, target]:
            hit = check(pathlib.Path(pubkey), pathlib.Path(sigdir), pathlib.Path(archive), prefix, pathlib.Path(target))
            print(f"hit={'true' if hit else 'false'}", file=sys.stderr)
            pathlib.Path(archive).unlink(missing_ok=True)
            return 0 if hit else 3
        case ["message", key, sha256]:
            sys.stdout.buffer.write(message(key, sha256))
        case ["entry", sigdir, prefix]:
            print(entry(pathlib.Path(sigdir), prefix))
        case _:
            print(__doc__, file=sys.stderr)
            return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
