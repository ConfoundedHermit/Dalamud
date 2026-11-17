#!/usr/bin/env python3
"""Fast-forward the upstream mirror and propose its integration into this fork."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


FORK_REPOSITORY = "ConfoundedHermit/DalamudPlus"
UPSTREAM_URL = "https://github.com/goatcorp/Dalamud.git"


def command(arguments, directory):
    result = subprocess.run(arguments, cwd=directory, text=True, encoding="utf-8",
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        raise RuntimeError(f"{arguments[0]} failed ({result.returncode}): {result.stderr.strip()}")
    return result.stdout.strip()


def git(directory, *arguments):
    return command(["git", *arguments], directory)


def is_ancestor(directory, older, newer):
    result = subprocess.run(["git", "merge-base", "--is-ancestor", older, newer],
                            cwd=directory, capture_output=True, text=True)
    if result.returncode not in (0, 1):
        raise RuntimeError(f"Cannot compare commit ancestry: {result.stderr.strip()}")
    return result.returncode == 0


def remote_heads(directory, remote, *branches):
    output = git(directory, "ls-remote", "--heads", remote,
                 *(f"refs/heads/{branch}" for branch in branches))
    return {line.split()[1]: line.split()[0] for line in output.splitlines()}


def pull_request(directory, repository, branch, main_commit, upstream_commit):
    existing = json.loads(command([
        "gh", "pr", "list", "--repo", repository, "--base", "main", "--head", branch,
        "--state", "all", "--limit", "1", "--json", "number,url,state",
    ], directory))
    if existing:
        # Respect a maintainer's decision to close this immutable integration PR.
        # A new pair of main/upstream revisions gets its own branch and PR.
        return {"url": existing[0]["url"], "state": existing[0]["state"], "created": False}

    body = (
        f"Merge upstream `goatcorp/Dalamud` master `{upstream_commit}` into fork main "
        f"`{main_commit}`.\n\n"
        "The mirror contains exact upstream commits. This integration branch contains a "
        "merge commit; no conflict preference, rebasing, or force push was used. "
        "Review the upstream delta and fork behavior before merging.\n\n"
        "Build/test and runtime validation remain required. If this PR was created with "
        "GITHUB_TOKEN, approve its workflow runs in GitHub; FORK_AUTOMATION_TOKEN can "
        "use a repository-scoped App/PAT to allow automatic CI.\n"
    )
    with tempfile.TemporaryDirectory(prefix="dalamud-upstream-pr-") as temporary:
        body_path = Path(temporary) / "body.md"
        body_path.write_text(body, encoding="utf-8")
        url = command([
            "gh", "pr", "create", "--repo", repository, "--base", "main", "--head", branch,
            "--title", f"Merge upstream master ({upstream_commit[:12]})",
            "--body-file", str(body_path),
        ], directory)
        return {"url": url, "state": "OPEN", "created": True}


def sync(directory, repository, upstream_url=UPSTREAM_URL, fork_remote="origin"):
    if repository != FORK_REPOSITORY:
        raise RuntimeError(f"Upstream integration is restricted to {FORK_REPOSITORY}.")
    if fork_remote.startswith("-") or upstream_url.startswith("-"):
        raise RuntimeError("Remote names and URLs cannot begin with a dash.")
    directory = Path(directory).resolve()
    if git(directory, "status", "--porcelain"):
        raise RuntimeError("Upstream synchronization requires a clean checkout.")

    # Fetch only branch tips: inherited upstream tags remain untouched.
    git(directory, "fetch", "--no-tags", fork_remote,
        "+refs/heads/main:refs/remotes/upstream-sync/fork-main",
        "+refs/heads/master:refs/remotes/upstream-sync/fork-master")
    git(directory, "fetch", "--no-tags", upstream_url,
        "+refs/heads/master:refs/remotes/upstream-sync/upstream-master")
    main_commit = git(directory, "rev-parse", "refs/remotes/upstream-sync/fork-main")
    mirror_commit = git(directory, "rev-parse", "refs/remotes/upstream-sync/fork-master")
    upstream_commit = git(directory, "rev-parse", "refs/remotes/upstream-sync/upstream-master")
    if not is_ancestor(directory, mirror_commit, upstream_commit):
        raise RuntimeError("Fork master has diverged from upstream master; refusing to rewrite it.")

    original_commit = git(directory, "rev-parse", "HEAD")
    original_branch = git(directory, "rev-parse", "--abbrev-ref", "HEAD")
    branch = f"automation/upstream/{upstream_commit[:12]}-into-{main_commit[:12]}"
    proposed_commit = None
    branch_exists = False
    try:
        if not is_ancestor(directory, upstream_commit, main_commit):
            git(directory, "switch", "--detach", main_commit)
            try:
                git(directory, "-c", "user.name=github-actions[bot]", "-c",
                    "user.email=41898282+github-actions[bot]@users.noreply.github.com",
                    "merge", "--no-ff", "-m",
                    f"Merge upstream master {upstream_commit[:12]}", upstream_commit)
            except RuntimeError:
                # Leave no partial merge or publication when the fork needs manual resolution.
                git(directory, "merge", "--abort")
                raise RuntimeError("Upstream integration conflicts with main; resolve it manually.") from None
            proposed_commit = git(directory, "rev-parse", "HEAD")
            existing = remote_heads(directory, fork_remote, branch)
            if existing:
                branch_exists = True
                git(directory, "fetch", "--no-tags", fork_remote,
                    f"+refs/heads/{branch}:refs/remotes/upstream-sync/candidate")
                candidate = git(directory, "rev-parse", "refs/remotes/upstream-sync/candidate")
                expected_parents = f"{main_commit} {upstream_commit}"
                if (git(directory, "show", "-s", "--format=%P", candidate) != expected_parents
                        or git(directory, "rev-parse", f"{candidate}^{{tree}}")
                        != git(directory, "rev-parse", f"{proposed_commit}^{{tree}}")):
                    raise RuntimeError("The integration branch already exists with unexpected history.")
                proposed_commit = candidate

        # Detect stale inputs before publishing; non-forced atomic pushes also reject races.
        heads = remote_heads(directory, fork_remote, "main", "master", branch)
        if (heads.get("refs/heads/main") != main_commit
                or heads.get("refs/heads/master") != mirror_commit):
            raise RuntimeError("Fork main or master changed during synchronization; retry with fresh tips.")
        if branch_exists and heads.get(f"refs/heads/{branch}") != proposed_commit:
            raise RuntimeError("The integration branch changed during synchronization; retry.")
        if not branch_exists and proposed_commit and f"refs/heads/{branch}" in heads:
            raise RuntimeError("The integration branch appeared during synchronization; retry.")

        refspecs = []
        if mirror_commit != upstream_commit:
            refspecs.append(f"{upstream_commit}:refs/heads/master")
        if proposed_commit and not branch_exists:
            refspecs.append(f"{proposed_commit}:refs/heads/{branch}")
        if refspecs:
            git(directory, "push", "--atomic", fork_remote, *refspecs)

        result = {"master": upstream_commit, "main": main_commit,
                  "mirror_updated": mirror_commit != upstream_commit,
                  "branch": branch if proposed_commit else None, "pull_request": None,
                  "pull_request_state": None, "pull_request_created": False}
        if proposed_commit:
            pr = pull_request(directory, repository, branch, main_commit, upstream_commit)
            result.update(pull_request=pr["url"], pull_request_state=pr["state"],
                          pull_request_created=pr["created"])
        return result
    finally:
        git(directory, "switch", original_branch if original_branch != "HEAD" else "--detach",
            *([original_commit] if original_branch == "HEAD" else []))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", ""))
    parser.add_argument("--directory", default=".")
    parser.add_argument("--upstream-url", default=UPSTREAM_URL)
    parser.add_argument("--fork-remote", default="origin")
    arguments = parser.parse_args()
    try:
        result = sync(arguments.directory, arguments.repository,
                      arguments.upstream_url, arguments.fork_remote)
        print(json.dumps(result, indent=2))
        summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary_path:
            with open(summary_path, "a", encoding="utf-8") as summary:
                summary.write(f"## Upstream sync\n\nMirror: `{result['master']}`\n\n")
                if result["pull_request"]:
                    action = "Created" if result["pull_request_created"] else "Existing"
                    summary.write(f"{action} integration PR ({result['pull_request_state']}): "
                                  f"{result['pull_request']}\n")
                else:
                    summary.write("Fork main already contains this upstream revision.\n")
    except (RuntimeError, OSError, ValueError) as error:
        print(f"Upstream synchronization failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
