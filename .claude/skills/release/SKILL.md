---
name: release
description: Cut a Longshot or Longshot Launcher release - `/release rc` tags the next release candidate on main, `/release stable` tags the tested rc's commit - then waits for CI and writes the release page's What's new. Use when the user asks to cut, tag or ship a release, an rc or a stable.
argument-hint: rc | stable
---

# Cutting a release

The same file in Longshot and LongshotLauncher. The process is [RELEASING.md](../../../RELEASING.md); this adds the
"What's new" CI doesn't write. Touch nothing but the tag and that summary: CI builds, signs and publishes. No argument:
ask `rc` or `stable`.

**Setup.** `$R` is the remote for `https://git.magicbean.dev/MagicBeanDev/<repo>.git` (`forge` or `origin`, never a
fork). `<Longshot>` is Longshot's checkout (this one, or `../Longshot` from the launcher's). Signed API calls go through
`sys.path.insert(0, "<Longshot>/scripts"); import forge; forge.api(method, "/repos/...", body)`. Check that
`python -I <Longshot>/scripts/forge.py whoami` prints a user (else the user runs `forge.py login`), and never touch the
token. In auto mode git.magicbean.dev goes in `allowed_domains`.

```sh
jj git fetch --remote $R && jj new main@$R
export GIT_CLIFF=$(python -I scripts/release.py git-cliff ~/.cache/longshot-cliff)
```

## 1. Before an rc (Longshot only)

A stable takes the rc's commit, so this happens only before an rc. Ask the user (AskUserQuestion, multi-select) which
of these to refresh first. Each is a pull request that must merge before the tag:

- **The pipeline tour and the play profile**: `tour.bin.xz` (docs/development.md, *The pipeline tour*) and
  `assets/profiles/longshot_vk.profdata.xz` (*The renderer's play profile*). One run records both: a counting runtime
  (`RX_VK_PGO=gen`), `pipeline_tour.py tour ... --publish --vk-profile <tour dir>/vk`, then
  `pipeline_tour.py merge-profile <tour dir>/vk --publish`. Linux, the user's game folder, hours of unattended play.
- **The build times**: rebuilt as a player would and the quoted numbers updated together (docs/development.md,
  *Measure them again*).

Skip what they decline. Then go on from `main@$R` with those merged.

## 2. Tag

- **rc:** `python -I scripts/release.py next` gives the tag and its notes; the commit is `main@$R`.
- **stable:** the newest rc tag (`git tag --sort=-v:refname -l 'v*-rc.*' | head -1`) without `-rc.N`, on that rc's
  commit (`jj log -r <rc> --no-graph -T commit_id`). Say how many commits main has past it (they don't ship).

Read the notes. Stop if a title is wrong (RELEASING.md, step 1); otherwise go on without asking.

```sh
git tag -a <tag> -m "<Longshot | Longshot Launcher> <tag>" <commit>   # jj doesn't push tags
python -I scripts/release.py check-tag <tag>                           # refused: git tag -d <tag>, report, stop
git push $R <tag>
```

## 3. Wait for CI

Monitor (not a foreground sleep) `GET /api/v1/repos/MagicBeanDev/<repo>/actions/tasks?limit=10` (public) for the jobs
whose `head_branch` is the tag. Print each new status, and stop on `failure`/`cancelled` (report with RELEASING.md's
*Troubleshooting*) or once `/releases/tags/<tag>` answers 200. Allow about 30 minutes for the launcher and 90 for
Longshot. The summary can go in as soon as *Sign and publish* runs: the draft is found through
`forge.api("GET", "/repos/.../releases?limit=10")`.

## 4. What's new

It goes between the first `---` (under the downloads) and `## <tag>`. Write it for players from the notes (for a
stable: everything since the last stable; read a PR when a title isn't enough). An rc after the first has two lists,
`## <tag> (<date>), new since rc.N` and `## Earlier release candidates: ...`: summarize the first, what's new for
someone on the rc before. Copy the shape of the repository's earlier summaries. Read the newest ones first:

- **Launcher** (v1.0.0-rc.4, rc.6): `## What's new`, three to six bullets each opening with a bold phrase and a full
  stop, then a `**Fixes:**` bullet with one short line per fix.
- **Longshot** (v3.0.0-rc.4): `## What's new in rc.N` (a stable: `in v3.1.0`), a bold heading per theme (**A faster
  game:**) with its headline and short bullets of measured numbers, then **New:** and **Fixed:** lists, and a closing
  `---` of its own.

Biggest first. Leave out tests, CI and docs unless a player sees them (a refactor that drops a CPU requirement counts).
Plain words, no marketing, no emoji, nothing the notes don't support.

```python
rel = next(r for r in forge.api("GET", "/repos/%s/releases?limit=10" % repo) if r["tag_name"] == tag)
body = rel["body"]; i = body.index("\n## %s " % tag); j = body.index("\n---\n"); assert j < i
forge.api("PATCH", "/repos/%s/releases/%d" % (repo, rel["id"]),
          {"body": body[:j] + "\n---\n\n" + summary.strip() + "\n\n" + body[i + 1:]})   # body only
```

That replaces any summary already there. Read the body back afterwards. CI's publish keeps the body, but a re-run
rewrites it, so add the summary again after one.

## 5. Report

Once published, `python -I scripts/release.py channels` shows the tag on rc (a stable on stable and rc), and the page
has every file, `SHA256SUMS` and `.sig`. Give the user the link and the summary, plus what's left to them: download
and `release.py verify`, testers (Longshot: a Windows tester before a stable), and after an rc, `/release stable` once
it's tested.
