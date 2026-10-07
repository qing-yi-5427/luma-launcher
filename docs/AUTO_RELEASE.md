# Automatic single-file releases

## Branch source pushes

When this workflow configuration is present on the pushed branch, pushing to
`main` or `mimo` runs **Build and validate**:

1. Build Release, run the isolated regression suite, publish the self-contained
   Windows x64 EXE with `PublishProfile=SingleFile`.
2. Require exactly one `Luma.exe` before generating its SHA-256 companion; enforce
   the existing 100 MiB artifact ceiling.
3. Upload `Luma.exe` and `Luma.exe.sha256` as an Actions artifact.
4. Only after successful validation, a separate write-enabled job downloads that
   exact artifact, checks its hash and creates a public GitHub **pre-release**.

Tags are unique per attempt: `build-<branch>-<run-number>-<attempt>-<short-commit>`. They do
not start with `v`, so they do not invoke the version-tag release workflow. Every
release points to the exact tested commit; concurrent pushes cannot move a shared
"latest branch" tag backwards. Rerunning a workflow creates a new release and never
overwrites a previous build's assets.
Old build releases are retained; no automatic deletion is performed.

These branch releases **never become Latest** and never bump the version inside
`Launcher.csproj`. The application's stable update check is not switched to dev
builds. Find them under the repository's full **Releases** list, not
`/releases/latest`.

Pull requests run validation only and cannot publish. Manual `workflow_dispatch`
is supported for the two named branches. The publishing job uses the built-in
`GITHUB_TOKEN` with job-local `contents: write`; no personal token is required.
Repository/organization policies must allow GitHub Actions and this permission.

## Version tags

Pushing a `v*` tag runs **Release** as before:

- `v0.5.0` -> stable release, eligible for Latest.
- `v0.5.0-rc.1` or `v0.5.0-mimo.1` -> pre-release, never Latest.

The tag workflow builds/tests/publishes the tagged source and attaches the EXE and
checksum. Before building, it validates the tag format and requires its numeric
version to match `Launcher.csproj`. Stable releases use GitHub's version-based
Latest selection rather than unconditionally replacing Latest.
It does not merge branches. Use monotonically increasing stable version
tags; do not republish an old stable tag as Latest.

## Manually publishing an already validated binary

For 0.6.0, the self-contained `Luma.exe` and `Luma.exe.sha256` were validated
from source commit `db41f6c`, and a draft `v0.6.0` release targeted that commit.
Creating a draft with a **new tag** through the GitHub API or `gh release create`
can itself emit a tag-push event. It did so for `v0.6.0` (Release workflow run
`37627067851`), even without a separate `git push` command. That workflow can
publish or replace assets under the same tag, so an earlier asset-digest check
is not sufficient evidence for the final public release.

When manually publishing an already validated binary, inspect Actions for a
same-tag Release job and stop a redundant publisher before restoring the intended
assets. Wait for the competing job to finish, then compare the **final** GitHub
asset digest, `Luma.exe.sha256`, local validated binary and installed EXE. Check
the tag target and public Latest status after that final verification. The 0.6.0
release requires this manual final-asset check; documentation-only commits after
`db41f6c` are not the binary's source commit.

## Branch rollout

This configuration is integrated into `main`. The same behavior applies to `mimo`
only after that branch contains these workflow changes. Publishing a build does
not merge branches or change application versions.
