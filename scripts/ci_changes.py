#!/usr/bin/env python3
"""Whether a pull request needs ci.yml's build jobs (build-test, package), from its files (docs/ci.md, Forgejo Actions).

The rule is the one MagicBeanDev/Longshot's scripts/ci_changes.py has: the jobs run unless every changed file is in
DOCS_ONLY, a place none of them reads (the tests read the source, the fixtures and Longshot's checkout, never these).
The packaging check (scripts/package.sh and the Windows program under Wine) runs only when a changed file is one of
PACKAGE: what the published programs are made from beyond the plain build, and the job itself. Anything unsure runs
everything: a push, a run by hand, an API that does not answer, a pull request of more files than the API lists.

  python3 -I scripts/ci_changes.py --server URL --repo OWNER/NAME --pr N      prints code=true|false and
                                                                              package=true|false, and appends them to
                                                                              $GITHUB_OUTPUT when that is set

ci.yml runs the base's copy of this file, never the pull request's: a pull request can't skip its own checks. Standard
library only; it reads the pull request's file list from the API (FORGEJO_TOKEN when set, else anonymous). No game data
is involved.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import os
import sys
import urllib.request

# (what build-test and package never read: the docs, the top-level documents, CircleCI's files (the lint job, which
# always runs, reads its gitleaks.toml), the forge's templates and the title's workflow)
DOCS_ONLY = (
    "docs/*",
    "AGENTS.md",
    "CONTRIBUTING.md",
    "README.md",
    "RELEASING.md",
    "LICENSE",
    ".circleci/*",
    ".chunk/*",
    ".forgejo/ISSUE_TEMPLATE/*",
    ".forgejo/pull_request_template.md",
    ".forgejo/workflows/pr-title.yml",
)
# (what scripts/package.sh's self-contained, single-file programs read beyond the plain build: the script, the app's
# project, manifest and Windows code, the packages and the SDK, and this job's own files)
PACKAGE = (
    "scripts/package.sh",
    "scripts/ci_dotnet.sh",
    "scripts/ci_changes.py",
    "App/LongshotLauncher.csproj",
    "App/app.manifest",
    "App/Windows*",
    "App/SetupWindows.cs",
    "App/packages.lock.json",
    "Core/*.csproj",
    "Core/packages.lock.json",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "NuGet.Config",
    "global.json",
    ".forgejo/workflows/ci.yml",
)
PAGE = 50
MAX_PAGES = 60  # (3000 files: past that, run everything)


def needs_code(files: list[str]) -> bool:
    """True when any changed file may change what build-test checks (and for no file at all: unsure)."""
    if not files:
        return True
    return any(not any(fnmatch.fnmatchcase(f, p) for p in DOCS_ONLY) for f in files)


def needs_package(files: list[str]) -> bool:
    """True when any changed file may change what scripts/package.sh makes (and for no file at all: unsure)."""
    if not files:
        return True
    return any(fnmatch.fnmatchcase(f, p) for f in files for p in PACKAGE)


def pr_files(server: str, repo: str, pr: int, token: str) -> list[str] | None:
    """Every path the pull request changes (renames by both names), or None when the API doesn't say in full."""
    files: list[str] = []
    for page in range(1, MAX_PAGES + 1):
        req = urllib.request.Request(f"{server}/api/v1/repos/{repo}/pulls/{pr}/files?limit={PAGE}&page={page}")
        req.add_header("Accept", "application/json")
        if token:
            req.add_header("Authorization", f"token {token}")
        with urllib.request.urlopen(req, timeout=60) as r:
            batch = json.load(r)
        if not batch:
            return files
        for item in batch:
            files.append(item["filename"])
            if item.get("previous_filename"):
                files.append(item["previous_filename"])
        if len(batch) < PAGE:
            return files
    return None


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--server", required=True)
    ap.add_argument("--repo", required=True)
    ap.add_argument("--pr", type=int, required=True)
    args = ap.parse_args(argv)
    try:
        files = pr_files(args.server.rstrip("/"), args.repo, args.pr, os.environ.get("FORGEJO_TOKEN", ""))
    except Exception as e:  # (any failure: unsure, so everything runs)
        print(f"::warning::ci_changes: the pull request's files are unknown ({e}): every job runs")
        files = None
    code = True if files is None else needs_code(files)
    package = True if files is None else (code and needs_package(files))
    if files is not None:
        print(f"ci_changes: {len(files)} paths; the build and tests {'run' if code else 'are not needed (docs only)'}")
        print(f"ci_changes: the packaging check {'runs' if package else 'is not needed (no packaging file)'}")
    lines = [f"code={'true' if code else 'false'}", f"package={'true' if package else 'false'}"]
    print("\n".join(lines))
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a", encoding="utf-8") as f:
            f.write("".join(line + "\n" for line in lines))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
