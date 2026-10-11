#!/usr/bin/env python3
"""Preserve the licensing metadata shipped with the published NuGet dependencies."""

import json
from pathlib import Path
import shutil
import sys
import xml.etree.ElementTree as ET


def collect(deps_file: Path, package_cache: Path, destination: Path) -> None:
    libraries = json.loads(deps_file.read_text(encoding="utf-8"))["libraries"]
    destination.mkdir(parents=True, exist_ok=True)
    inventory = []
    for identity, library in sorted(libraries.items()):
        if library["type"] not in ("package", "runtimepack"):
            continue
        package_id, version = identity.removeprefix("runtimepack.").rsplit("/", 1)
        package_directory = package_cache / package_id.lower() / version.lower()
        manifests = sorted(package_directory.glob("*.nuspec"))
        if not manifests:
            raise FileNotFoundError(f"NuGet package metadata missing: {package_directory}")
        manifest = manifests[0]
        metadata = ET.parse(manifest).getroot().find("{*}metadata")
        if metadata is None:
            raise ValueError(f"NuGet metadata missing in {manifest}")
        license_node = metadata.find("{*}license")
        notice_files = {
            path for path in package_directory.iterdir()
            if path.is_file() and path.name.casefold().startswith(
                ("license", "notice", "copying", "third-party-notices", "thirdpartynotices")
            )
        }
        if license_node is not None and license_node.get("type") == "file":
            license_file = (package_directory / (license_node.text or "")).resolve()
            if not license_file.is_relative_to(package_directory.resolve()):
                raise ValueError(f"License path escapes package: {identity}")
            notice_files.add(license_file)
        package_destination = destination / f"{package_id}-{version}"
        package_destination.mkdir()
        for path in sorted(notice_files | {manifest}):
            shutil.copyfile(path, package_destination / path.name)
        inventory.extend([
            f"{package_id} {version}",
            f"License: {metadata.findtext('{*}license', default='See package metadata')}",
            f"Copyright: {metadata.findtext('{*}copyright', default='See package metadata')}",
            "",
        ])
    (destination / "PACKAGES.txt").write_text("\n".join(inventory), encoding="utf-8")


if __name__ == "__main__":
    collect(*(Path(argument) for argument in sys.argv[1:]))
