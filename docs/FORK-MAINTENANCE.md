# Maintaining Dalamud Plus

Dalamud Plus is the independent soft fork at [ConfoundedHermit/DalamudPlus](https://github.com/ConfoundedHermit/DalamudPlus). Fork changes are maintained here without routine PR submission to upstream.

## Branch contract

| Branch | Purpose | Local tracking |
| --- | --- | --- |
| `main` | Fork development, upstream integration, and releases. | `origin/main`. |
| `master` | Exact upstream history, with no fork-only commits. | `goatcorp/master`, with fast-forward-only merges. |
| Existing feature and backup branches | Preserved development and review history. | Existing tracking retained. |

The fork integration starts at commit `67116866f0e8fc0bd6d3aa753bdc2cee96ee8613`, based on upstream `43b34118bcf7213e575ec39517d033191b1acf17`. The original integration's recorded build and 312 passing tests apply to that exact integration commit.

Use `origin` for `https://github.com/ConfoundedHermit/DalamudPlus.git` and `goatcorp` for `https://github.com/goatcorp/Dalamud.git`. The default push remote is `origin`, with `push.default=simple`; updates to the clean mirror are explicit.

## Automation

| Workflow | Behavior |
| --- | --- |
| `main.yml` | Debug/Release builds, tests, build artifacts, and API compatibility checks on PRs. |
| `release.yml` | Builds and tests a published fork tag, then uploads the ZIP and checksum. |
| `generate-changelog.yml` | Generates notes for explicit fork revisions, including direct commits; publishes a generated section in GitHub releases while preserving hand-written notes. |
| `upstream-sync.yml` | Checks upstream every six hours or on manual dispatch, fast-forwards the clean `master` mirror, and opens a reviewable integration PR into this fork's `main`. |
| `update-submodules.yml` | Optional independent ClientStructs/schema updates through PRs into `main`. Manual runs are available; scheduled runs require `ENABLE_SUBMODULE_UPDATES=true`. |
| `backup.yml` | Optional branch/tag backups to fork-owned GitLab/Codeberg repositories. Scheduled runs require `ENABLE_MIRROR_BACKUP=true`; manual runs also require configured destinations and keys. |

All three scheduled workflows use `0 */6 * * *`: 00:00, 06:00, 12:00, and 18:00 UTC. Submodule checks and backups retain their opt-in variables. Every checkout step uses the same pinned checkout v4.3.1 revision.

Build CI has read-only repository permissions and builds/tests the fork. Its PR API check downloads upstream's staging binaries as a read-only compatibility baseline. Certificate signing, upstream staging deployment, and the inherited upstream-only attestation step are absent from fork CI. Release packaging publishes to this repository through `release.yml`.

Upstream sync stops on mirror divergence or merge conflicts. Its integration branches preserve the selected `main` and upstream revisions; it never force-pushes `main` or `master`. Review and validate integration PRs before merging. Submodule automation is useful when the fork intentionally tracks dependencies ahead of upstream; ordinary upstream merges already import upstream's selected gitlinks.

Allow GitHub Actions to create pull requests in this repository's Actions settings. Sync and submodule jobs use `GITHUB_TOKEN` by default. GitHub requires approval before running CI for PRs created with that token. For automatic CI, configure the optional `FORK_AUTOMATION_TOKEN` secret with access to this fork and Contents, Pull requests, and Workflows write permissions. Workflows permission is needed when an upstream update changes workflow files. The token must also satisfy branch rules; failed pushes stop the run. See [GitHub's token event behavior](https://docs.github.com/en/actions/concepts/security/github_token) and [workflow-file permissions](https://docs.github.com/en/rest/repos/contents#create-or-update-file-contents).

Configure optional mirror destinations with repository variables `MIRROR_GITLAB_URL` and/or `MIRROR_CODEBERG_URL`, using SSH URLs for repositories you control, and the matching `MIRROR_GITLAB_SYNC_KEY` / `MIRROR_CODEBERG_SYNC_KEY` secrets. For example, use `git@gitlab.com:<owner>/<repo>.git` or `git@codeberg.org:<owner>/<repo>.git`. Backup pushes are non-forcing and do not delete remote refs; a diverged mirror needs manual attention.

The upstream `tag-build.yml` and `rollup.yml` workflows are removed from `main`: fork releases use explicit tags, and upstream's `api16` rollup does not serve this fork. Keep those workflow IDs disabled at repository level because pristine `master` retains their original definitions.

## Ingest upstream changes

Review the PR from `upstream-sync.yml` when the workflow can merge cleanly. If it stops on a merge conflict, prepare a separate integration branch from the current fork `main`. Start with a clean worktree and check each command's result before continuing.

```powershell
git fetch --no-tags origin
git fetch --no-tags goatcorp master
git switch master
git merge-base --is-ancestor master goatcorp/master
git merge --ff-only goatcorp/master
$integrationBranch = "integration/upstream-$(git rev-parse --short master)"
git switch -c $integrationBranch origin/main
git merge --no-ff master
```

Stop if upstream history cannot fast-forward `master`. Resolve any conflicts on the integration branch and commit the merge there. Review the upstream delta, fork behavior, workflows, version property, and runtime changes. Carry forward the fork's workflow adaptations and retain repository-level controls for the retired workflows still present on `master`.

```powershell
git submodule update --init --recursive
.\generate.cmd vs2026
.\build.cmd Release
dotnet test Dalamud.Test/Dalamud.Test.csproj -c Release -tl:off -p:WarningLevel=0
```

If the fork's remote mirror needs advancing, fast-forward it with `git push origin master:master`. Confirm the remote `main` is still the base used to build the candidate, then publish the integration branch and open a draft PR for review.

```powershell
git push -u origin $integrationBranch
gh pr create --repo ConfoundedHermit/DalamudPlus --base main --head $integrationBranch --draft
```

After review and CI, land the PR with a merge commit so the next sync can recognize the imported upstream history. Keep `master` identical to upstream.

## Fork releases

Assembly versioning and Git tag names are separate. `Dalamud/Dalamud.csproj` defines the pinned `DalamudVersion` for each revision. Existing upstream tag `15.0.4.0` identifies `51b4552f6` and must retain that target. Fork release tags use the date-based format **`dalamud-plus/vYYYYMMDD`**, with a positive numeric suffix for another release on the same date, such as `dalamud-plus/v20261116-2`.

From a clean, reviewed `main`, create an annotated date tag locally:

```powershell
.\tools\release.ps1 -TagName dalamud-plus/v20261116
# For another release on the same date:
.\tools\release.ps1 -TagName dalamud-plus/v20261116-2
```

The helper validates the calendar date and reads the pinned framework version for the annotation. It leaves the project, index, and current commit unchanged and never pushes. Review the tag, build and test that exact revision, and push the selected branch and tag explicitly when ready. Publish a GitHub release against the existing fork tag only after that review.

The release workflow requires the fork tag prefix, a tagged revision containing the original integration commit, and that revision's inclusion in remote `main` history. These checks reject clean upstream mirror revisions and tags unrelated to the fork. The workflow builds/tests and uploads `Dalamud-win-x64.zip` and its SHA-256 companion to the release in `github.repository`. It uses the job's `GITHUB_TOKEN` with `contents: write`.

The changelog workflow applies the same provenance checks. Tag pushes produce a downloadable Markdown artifact; published releases also receive a generated notes section. Manual dispatch accepts an exact fork `tag` and an optional ancestral `previous_tag`; otherwise it selects the nearest reachable fork release. The first fork release starts from the original upstream baseline. Direct commits and merge commits are included, and text outside the generated section is preserved.

Keep DLL names, .NET namespaces, plugin identifiers, configuration directories, ZIP root layout, dependency submodules, assets, and upstream protocol/distribution endpoints compatible. Project branding and fork support links can change independently. Load a local fork build with the fork's injector or the launcher's explicit Dalamud directory override; see [running the fork](../README.md#running-the-fork). This repository does not provide an automatic launcher update channel.

Build/test success covers compilation and exercised fixtures. Record in-game startup/unload, graphics/ReShade, plugin behavior, and Windows/Linux/macOS runtime checks separately. Hosted release upload and download verification remain part of the first actual publication.
