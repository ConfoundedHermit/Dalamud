#!/usr/bin/env python3
"""Generate fork release notes offline, or publish their managed GitHub section."""

import argparse
import html
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from urllib.parse import quote


FORK_REPOSITORY = "ConfoundedHermit/DalamudPlus"
TAG_PREFIX = "dalamud-plus/v"
FIRST_RELEASE_BASELINE = "43b34118bcf7213e575ec39517d033191b1acf17"
SECTION_START = "<!-- dalamud-plus:changelog:start -->"
SECTION_END = "<!-- dalamud-plus:changelog:end -->"


def command(*arguments):
    result = subprocess.run(arguments, capture_output=True, text=True, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"{arguments[0]} failed: {result.stderr.strip()}")
    return result.stdout.rstrip("\n")


def git(*arguments):
    return command("git", *arguments)


def validate_tag(tag):
    if not tag.startswith(TAG_PREFIX):
        raise RuntimeError(f"Fork tags must start with {TAG_PREFIX}.")
    git("check-ref-format", f"refs/tags/{tag}")


def resolve_tag(tag):
    validate_tag(tag)
    return git("rev-parse", "--verify", "--end-of-options", f"refs/tags/{tag}^{{commit}}")


def is_ancestor(ancestor, descendant):
    result = subprocess.run(
        ["git", "merge-base", "--is-ancestor", ancestor, descendant],
        capture_output=True, text=True, encoding="utf-8",
    )
    if result.returncode not in (0, 1):
        raise RuntimeError(f"Cannot compare revisions: {result.stderr.strip()}")
    return result.returncode == 0


def release_range(tag, previous_tag=None, baseline=FIRST_RELEASE_BASELINE):
    target = resolve_tag(tag)
    if previous_tag:
        previous = resolve_tag(previous_tag)
        if previous_tag == tag or not is_ancestor(previous, target):
            raise RuntimeError("The previous fork tag must be a different tag ancestral to the target.")
        return previous_tag, previous, target

    # Compare graph distance rather than tag dates or mixed upstream version names.
    candidates = []
    for candidate in git("tag", "--list", f"{TAG_PREFIX}*").splitlines():
        revision = resolve_tag(candidate)
        if revision == target or not is_ancestor(revision, target):
            continue
        distance = int(git("rev-list", "--count", f"{revision}..{target}"))
        candidates.append((distance, candidate, revision))
    if candidates:
        _, previous_tag, previous = min(candidates)
        return previous_tag, previous, target

    previous = git("rev-parse", "--verify", "--end-of-options", f"{baseline}^{{commit}}")
    if not is_ancestor(previous, target):
        raise RuntimeError("The first-release baseline is not an ancestor of the target tag.")
    return previous, previous, target


def markdown_text(value):
    value = html.escape(value, quote=False).replace("@", "&#64;")
    return re.sub(r"([\\`*_{}\[\]()])", r"\\\1", value)


def generate_notes(tag, repository, previous_tag=None, baseline=FIRST_RELEASE_BASELINE):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise RuntimeError("Repository must be an owner/name pair.")
    previous_label, previous, target = release_range(tag, previous_tag, baseline)
    lines = [
        f"## Dalamud Plus {markdown_text(tag[len(TAG_PREFIX):])}",
        "",
        f"Changes from `{previous_label}` to `{tag}`.",
        f"[Compare revisions](https://github.com/{repository}/compare/{quote(previous_label, safe='')}...{quote(tag, safe='')})",
        "",
    ]
    if previous_label == previous:
        lines += ["This first fork release includes changes since the original upstream baseline.", ""]
    lines += ["### Commits", ""]
    commits = git("log", "--reverse", "--topo-order", "--format=%H%x00%s%x00%an%x00%P", f"{previous}..{target}")
    for entry in commits.splitlines():
        revision, subject, author, parents = entry.split("\0", 3)
        merge = " (merge)" if len(parents.split()) > 1 else ""
        lines.append(
            f"- {markdown_text(subject)}{merge} "
            f"([`{revision[:9]}`](https://github.com/{repository}/commit/{revision})) "
            f"by {markdown_text(author)}"
        )
    if not commits:
        lines.append("No new commits in this range.")
    return "\n".join(lines) + "\n"


def merge_generated_notes(body, notes):
    if SECTION_START in notes or SECTION_END in notes:
        raise RuntimeError("Generated notes must not contain managed-section markers.")
    block = f"{SECTION_START}\n{notes.rstrip()}\n{SECTION_END}"
    starts, ends = body.count(SECTION_START), body.count(SECTION_END)
    if starts == ends == 0:
        separator = "" if not body or body.endswith("\n\n") else "\n" if body.endswith("\n") else "\n\n"
        return f"{body}{separator}{block}\n"
    if starts != 1 or ends != 1 or body.index(SECTION_END) < body.index(SECTION_START):
        raise RuntimeError("Release notes contain malformed or duplicate managed-section markers.")
    start, end = body.index(SECTION_START), body.index(SECTION_END) + len(SECTION_END)
    return body[:start] + block + body[end:]


def publish_notes(tag, repository, notes):
    # Never infer a destination from a fork-network parent or the caller's gh defaults.
    if repository != FORK_REPOSITORY:
        raise RuntimeError(f"Release notes may only be published to {FORK_REPOSITORY}.")
    validate_tag(tag)
    release = json.loads(command("gh", "release", "view", tag, "--repo", repository, "--json", "body,tagName,isDraft"))
    if release["tagName"] != tag or release["isDraft"]:
        raise RuntimeError("The selected release must be published and match the requested fork tag.")
    original = release.get("body") or ""
    updated = merge_generated_notes(original, notes)
    if updated == original:
        print(f"Release notes for {tag} are already current.")
        return
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="", suffix=".md", delete=False) as stream:
        stream.write(updated)
        notes_file = Path(stream.name)
    try:
        command("gh", "release", "edit", tag, "--repo", repository, "--notes-file", str(notes_file))
    finally:
        notes_file.unlink()
    print(f"Updated the generated section of {repository} release {tag}.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True, help="Exact dalamud-plus/v... target tag.")
    parser.add_argument("--previous-tag", help="Exact ancestral fork tag; otherwise select the nearest reachable fork release.")
    parser.add_argument("--baseline", default=FIRST_RELEASE_BASELINE, help="First-release starting revision when no earlier fork tag exists.")
    parser.add_argument("--repository", default=FORK_REPOSITORY, help="Repository for commit/comparison links; publication requires the Dalamud Plus fork.")
    parser.add_argument("--output", type=Path, default=Path("release-notes.md"), help="Write generated Markdown to this file.")
    parser.add_argument("--publish-notes-file", type=Path, help="Publish this existing file as a managed section, preserving other release text.")
    args = parser.parse_args()
    try:
        if args.publish_notes_file:
            if args.previous_tag:
                parser.error("--previous-tag applies only to generation.")
            publish_notes(args.tag, args.repository, args.publish_notes_file.read_text(encoding="utf-8"))
        else:
            notes = generate_notes(args.tag, args.repository, args.previous_tag, args.baseline)
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(notes, encoding="utf-8", newline="\n")
            print(f"Wrote {args.output} for {args.tag}.")
    except (RuntimeError, OSError, ValueError, KeyError) as error:
        print(f"Changelog failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
