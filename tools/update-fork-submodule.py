#!/usr/bin/env python3
"""Prepare an immutable dependency-update PR into this fork's main branch."""

import argparse
import json
from pathlib import Path
import subprocess
import tempfile


SUBMODULES = {
    "clientstructs": ("lib/FFXIVClientStructs", "main", "ClientStructs"),
    "excel": ("lib/Lumina.Excel", "master", "Excel schema"),
}
FORK = "ConfoundedHermit/DalamudPlus"


def run(*args, cwd=None):
    return subprocess.run(args, cwd=cwd, check=True, text=True, capture_output=True).stdout.strip()


def prepare(repository, submodule):
    if repository != FORK:
        raise ValueError("Dependency PRs must target ConfoundedHermit/DalamudPlus.")
    root = Path(run("git", "rev-parse", "--show-toplevel"))
    if run("git", "status", "--porcelain", cwd=root):
        raise ValueError("Start with a clean checkout.")
    run("git", "fetch", "--no-tags", "origin", "refs/heads/main:refs/remotes/origin/main", cwd=root)
    base = run("git", "rev-parse", "refs/remotes/origin/main", cwd=root)
    path, dependency_branch, title = SUBMODULES[submodule]
    old = run("git", "rev-parse", f"{base}:{path}", cwd=root)
    with tempfile.TemporaryDirectory(prefix="dalamud-submodule-") as directory:
        scratch = Path(directory) / "worktree"
        run("git", "worktree", "add", "--detach", str(scratch), base, cwd=root)
        try:
            run("git", "submodule", "update", "--init", "--", path, cwd=scratch)
            dependency = scratch / path
            run("git", "fetch", "--no-tags", "origin", f"refs/heads/{dependency_branch}", cwd=dependency)
            target = run("git", "rev-parse", "FETCH_HEAD^{commit}", cwd=dependency)
            if old == target:
                print(f"{title} is already current on main.")
                return
            branch = f"automation/{submodule}/{base}-{target}"
            advertised = run("git", "ls-remote", "--heads", "origin", f"refs/heads/{branch}", cwd=root)
            if advertised:
                run("git", "fetch", "--no-tags", "origin", f"refs/heads/{branch}", cwd=root)
                existing = run("git", "rev-parse", "FETCH_HEAD", cwd=root)
                parent = run("git", "rev-parse", f"{existing}^", cwd=root)
                changed = run("git", "diff-tree", "--no-commit-id", "--name-only", "-r", existing, cwd=root)
                gitlink = run("git", "rev-parse", f"{existing}:{path}", cwd=root)
                if (parent, changed, gitlink) != (base, path, target):
                    raise ValueError("The existing immutable update branch has unexpected content.")
            else:
                run("git", "checkout", "--detach", target, cwd=dependency)
                run("git", "add", "--", path, cwd=scratch)
                run("git", "-c", "user.name=github-actions[bot]", "-c",
                    "user.email=41898282+github-actions[bot]@users.noreply.github.com", "commit",
                    "-m", f"Update {title}", "--only", "--", path, cwd=scratch)
                live_main = run("git", "ls-remote", "--heads", "origin", "refs/heads/main", cwd=root).split()[0]
                if live_main != base:
                    raise ValueError("main advanced during preparation; rerun against its new revision.")
                run("git", "push", "origin", f"HEAD:refs/heads/{branch}", cwd=scratch)
            pulls = json.loads(run("gh", "pr", "list", "--repo", repository, "--base", "main",
                                   "--head", branch, "--state", "all", "--json", "number,state,url", cwd=root))
            if pulls:
                print(f"An update PR already exists: {pulls[0]['url']} ({pulls[0]['state']}).")
                return
            body = Path(directory) / "pr-body.md"
            body.write_text(
                f"Updates {title} in `{path}` from `{old}` to `{target}`.\n\n"
                "This PR targets the fork's `main`; the upstream mirror is unchanged. "
                "Review the dependency delta and build checks before merging.\n\n"
                "Runs created with GITHUB_TOKEN may require maintainer approval for CI. "
                "Configure FORK_AUTOMATION_TOKEN for automatic CI triggering.\n", encoding="utf-8")
            print(run("gh", "pr", "create", "--repo", repository, "--base", "main", "--head", branch,
                      "--title", f"Update {title}", "--body-file", str(body), cwd=root))
        finally:
            # scratch is a newly created disposable worktree, never main or master.
            run("git", "worktree", "remove", "--force", str(scratch), cwd=root)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--submodule", choices=SUBMODULES, required=True)
    parser.add_argument("--repository", default=FORK)
    arguments = parser.parse_args()
    try:
        prepare(arguments.repository, arguments.submodule)
    except (ValueError, subprocess.CalledProcessError) as error:
        message = error.stderr.strip() if isinstance(error, subprocess.CalledProcessError) and error.stderr else str(error)
        parser.exit(1, f"Dependency update failed: {message}\n")
