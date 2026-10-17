#!/usr/bin/env python3
"""Fail when a tracked Markdown file links to a repository path that does not exist."""
from pathlib import Path
import re
import subprocess
import sys

repo = Path(__file__).resolve().parents[1]
tracked = subprocess.run(
    ["git", "-C", str(repo), "ls-files", "-z", "--cached", "--others", "--exclude-standard", "*.md"],
    check=True, capture_output=True, text=True,
).stdout.split("\0")

# Inline links and images: [text](target) or ![alt](target "title").
link = re.compile(r"!?\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+\"[^\"]*\")?\s*\)")
fence = re.compile(r"^\s*(```|~~~)")
external = re.compile(r"^[a-zA-Z][a-zA-Z0-9+.-]*:")

broken = []
for name in filter(None, tracked):
    source = repo / name
    if not source.is_file():
        continue
    in_fence = False
    for number, line in enumerate(source.read_text(encoding="utf-8").splitlines(), 1):
        if fence.match(line):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        for target in link.findall(re.sub(r"`[^`]*`", "", line)):
            if target.startswith("#") or external.match(target):
                continue
            path = re.sub(r":\d+(-\d+)?$", "", target.split("#", 1)[0])
            resolved = repo / path.lstrip("/") if path.startswith("/") else source.parent / path
            if not resolved.exists():
                broken.append(f"{name}:{number}: {target}")

if broken:
    print("broken Markdown links:", *broken, sep="\n  ", file=sys.stderr)
    sys.exit(1)
print("markdown links: PASS")
