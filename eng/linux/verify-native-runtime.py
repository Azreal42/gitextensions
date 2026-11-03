#!/usr/bin/env python3
"""Validate the private native runtime against the Debian package contract."""
import argparse
import json
from pathlib import Path
import re
import struct
import subprocess


def version(value):
    parts = tuple(int(part) for part in value.split("."))
    return parts + (0,) * (3 - len(parts))


def verify(directory, runtime, control):
    match = re.search(r"libc6\s*\(>=\s*([0-9.]+)\)", control.read_text())
    if not match:
        raise ValueError("The Debian control must declare its minimum libc6 version")
    floor = match.group(1)
    machine = {"linux-x64": 62, "linux-arm64": 183}[runtime]
    result = []
    for name in ("gitextensions-exec", "gitextensions-posix", "libgitextensions-posix.so"):
        path = directory / name
        with path.open("rb") as source:
            header = source.read(20)
        if len(header) != 20 or header[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<H", header, 18)[0] != machine:
            raise ValueError(f"Private process runtime {name} does not match {runtime}")
        if not name.endswith(".so") and path.stat().st_mode & 0o111 == 0:
            raise ValueError(f"Private process runtime {name} must be executable")
        output = subprocess.run(["readelf", "--wide", "--version-info", str(path)],
                                text=True, capture_output=True, check=True).stdout
        required = set(re.findall(r"Name:\s+(GLIBC_[\w.]+)", output))
        if not required:
            raise ValueError(f"Private process runtime {name} has no verifiable libc ABI")
        for symbol in sorted(required):
            value = symbol.removeprefix("GLIBC_")
            if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)+", value) or version(value) > version(floor):
                raise ValueError(f"Private process runtime {name} requires {symbol}, beyond declared libc6 {floor}")
        result.append({"file": name, "runtime": runtime, "declared_libc6": floor,
                       "required_glibc": sorted(required, key=lambda item: version(item.removeprefix("GLIBC_")))})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", required=True, type=Path)
    parser.add_argument("--runtime", required=True, choices=("linux-x64", "linux-arm64"))
    parser.add_argument("--control", required=True, type=Path)
    args = parser.parse_args()
    try:
        result = verify(args.directory, args.runtime, args.control)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, str(error) + "\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
