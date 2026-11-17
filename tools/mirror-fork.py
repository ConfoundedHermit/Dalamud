#!/usr/bin/env python3
"""Back up fork branches and tags to explicitly configured fork-owned forges."""

import argparse
import os
import re
import subprocess
from urllib.parse import urlsplit


def destinations(environment):
    configured = []
    for variable, host in (("MIRROR_GITLAB_URL", "gitlab.com"), ("MIRROR_CODEBERG_URL", "codeberg.org")):
        url = environment.get(variable, "").strip()
        if not url:
            continue
        scp = re.fullmatch(r"git@([^:]+):(.+)", url)
        if scp:
            destination_host, path = scp.groups()
        else:
            parsed = urlsplit(url)
            if parsed.scheme != "ssh" or parsed.username != "git" or parsed.password or parsed.query or parsed.fragment:
                raise ValueError(f"{variable} must be an SSH Git URL using the git account and a configured mirror key.")
            destination_host, path = parsed.hostname, parsed.path.lstrip("/")
        if destination_host != host or not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?", path):
            raise ValueError(f"{variable} must name an owner/repository on {host}.")
        if path.split("/", 1)[0].casefold() == "goatcorp":
            raise ValueError(f"{variable} must use a fork-owned destination, not goatcorp.")
        configured.append(url)
    if not configured:
        raise ValueError("Configure MIRROR_GITLAB_URL and/or MIRROR_CODEBERG_URL before running backup.")
    return configured


def run(*arguments):
    return subprocess.run(arguments, check=True, text=True, capture_output=True).stdout.strip()


def mirror(targets):
    run("git", "fetch", "--prune", "--tags", "origin", "+refs/heads/*:refs/remotes/origin/*")
    refs = run("git", "for-each-ref", "--format=%(refname) %(objectname)",
               "refs/remotes/origin", "refs/tags")
    refspecs = []
    for line in refs.splitlines():
        reference, revision = line.split(" ", 1)
        if reference == "refs/remotes/origin/HEAD":
            continue
        target = reference.replace("refs/remotes/origin/", "refs/heads/", 1)
        refspecs.append(f"{revision}:{target}")
    if not refspecs:
        raise ValueError("No branch or tag refs were found for backup.")
    for target in targets:
        run("git", "push", "--atomic", target, *refspecs)
    print(f"Backed up {len(refspecs)} branches/tags to {len(targets)} configured destination(s).")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--validate-only", action="store_true")
    arguments = parser.parse_args()
    try:
        targets = destinations(os.environ)
        if arguments.validate_only:
            print(f"Validated {len(targets)} configured fork-owned destination(s).")
        else:
            mirror(targets)
    except (ValueError, subprocess.CalledProcessError) as error:
        # Keep configured URLs and authentication details out of workflow errors.
        message = str(error) if isinstance(error, ValueError) else "Git backup failed; no force push was attempted."
        parser.exit(1, f"Backup failed: {message}\n")
