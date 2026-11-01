#!/usr/bin/env python3
"""Calculate Installed-Size in KiB independently of filesystem allocation."""

from pathlib import Path
import stat
import sys


def installed_size(root: Path) -> int:
    if not stat.S_ISDIR(root.lstat().st_mode):
        raise ValueError(f"Package payload must be a directory: {root}")
    # Debian Policy 5.6.20: each regular file/symlink rounded up to KiB,
    # and 1 KiB for every other object. Do not measure directory blocks or holes.
    total = 1  # The payload root directory.
    for path in root.rglob("*"):
        entry = path.lstat()
        total += (entry.st_size + 1023) // 1024 if stat.S_ISREG(entry.st_mode) or stat.S_ISLNK(entry.st_mode) else 1
    return total


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: package-installed-size.py PAYLOAD_DIRECTORY")
    print(installed_size(Path(sys.argv[1])))
