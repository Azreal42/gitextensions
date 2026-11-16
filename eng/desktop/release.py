#!/usr/bin/env python3
"""Publish the packages from successful master CI runs, without rebuilding them."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
from urllib.parse import urlencode
import zipfile


WORKFLOWS = ("linux.yml", "avalonia-platforms.yml", "app-build.yml")
DELIVERIES = (
    ("linux.yml", "gitextensions-linux-amd64", "linux-amd64", r"gitextensions_[0-9A-Za-z.~+-]+_amd64\.deb"),
    ("avalonia-platforms.yml", "avalonia-osx-arm64", "osx-arm64", r"gitextensions_[0-9A-Za-z.-]+_osx-arm64\.zip"),
    ("avalonia-platforms.yml", "avalonia-osx-x64", "osx-x64", r"gitextensions_[0-9A-Za-z.-]+_osx-x64\.zip"),
)


def gh(*arguments, **kwargs):
    return subprocess.run(["gh", *map(str, arguments)], check=True, **kwargs)


def api(path):
    return json.loads(gh("api", path, capture_output=True, text=True).stdout)


def validated_run(runs, repository, sha):
    candidates = [run for run in runs if run["head_sha"] == sha and run["head_branch"] == "master"
                  and run["event"] == "push" and run["head_repository"]["full_name"] == repository]
    if not candidates:
        return None
    latest = max(candidates, key=lambda run: run["id"])
    return latest if latest["status"] == "completed" and latest["conclusion"] == "success" else None


def extract_delivery(archive, artifact, destination, pattern, runtime, sha):
    digest = "sha256:" + hashlib.sha256(archive.read_bytes()).hexdigest()
    if artifact.get("digest") != digest:
        raise ValueError("The downloaded artifact does not match GitHub's SHA-256 digest.")
    with zipfile.ZipFile(archive) as outer:
        files = [entry for entry in outer.infolist() if not entry.is_dir()]
        if len(files) != 1 or not re.fullmatch(pattern, files[0].filename):
            raise ValueError("Expected exactly one delivery with its original platform filename.")
        target = destination / files[0].filename
        with outer.open(files[0]) as source, target.open("wb") as output:
            shutil.copyfileobj(source, output)
    if runtime.startswith("osx-"):
        with zipfile.ZipFile(target) as inner:
            manifest = json.loads(inner.read("delivery-manifest.json"))
        if manifest["source_commit"] != sha or manifest["runtime"] != runtime:
            raise ValueError("The native delivery does not belong to the validated commit and platform.")
    return target


def release_by_tag(repository, tag):
    result = subprocess.run(["gh", "api", f"repos/{repository}/releases/tags/{tag}"], capture_output=True, text=True)
    if result.returncode == 0:
        return json.loads(result.stdout)
    if "HTTP 404" in result.stderr:
        return None
    result.check_returncode()


def verify_release(release, sha, assets):
    if release["target_commitish"] != sha:
        raise ValueError("The release tag is already assigned to another source.")
    expected = {path.name: "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest() for path in assets}
    actual = {asset["name"]: asset.get("digest") for asset in release["assets"]}
    if actual != expected:
        raise ValueError("The uploaded release assets do not match the verified packages.")


def publish(repository, sha, assets, notes):
    tag = "avalonia-alpha-" + sha[:12]
    release = release_by_tag(repository, tag)
    if release is not None and release["target_commitish"] != sha:
        raise ValueError("Refusing to replace a tag assigned to another commit.")
    if release is not None and not release["draft"]:
        # A rerun can produce a different ZIP timestamp for the same source.
        # Keep the already verified, published delivery instead of replacing it.
        if {asset["name"] for asset in release["assets"]} != {path.name for path in assets}:
            raise ValueError("The published release is missing expected delivery assets.")
        return release["html_url"]
    if release is None:
        gh("release", "create", tag, "--repo", repository, "--target", sha, "--draft", "--prerelease",
           "--latest=false", "--title", f"Git Extensions macOS / Linux alpha ({sha[:8]})", "--notes-file", notes)
    if release is None or release["draft"]:
        gh("release", "upload", tag, *assets, "--repo", repository, "--clobber")
        verify_release(release_by_tag(repository, tag), sha, assets)
        gh("release", "edit", tag, "--repo", repository, "--draft=false", "--prerelease", "--latest=false")
    release = release_by_tag(repository, tag)
    verify_release(release, sha, assets)
    if release["draft"] or not release["prerelease"]:
        raise ValueError("The prerelease was not published.")
    return release["html_url"]


def summary(message):
    print(message, flush=True)
    if path := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(path, "a", encoding="utf-8") as output:
            output.write(message + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repository) or not re.fullmatch(r"[0-9a-f]{40}", args.sha):
        parser.error("Expected an owner/repository and a full lowercase commit SHA.")
    prefix = f"repos/{args.repository}"
    comparison = api(f"{prefix}/compare/{args.sha}...master")
    if comparison["status"] not in ("ahead", "identical"):
        raise ValueError("Only a commit on master may be published.")
    query = urlencode({"head_sha": args.sha, "branch": "master", "event": "push", "per_page": 100})
    runs = {}
    for workflow in WORKFLOWS:
        runs[workflow] = validated_run(api(f"{prefix}/actions/workflows/{workflow}/runs?{query}")["workflow_runs"], args.repository, args.sha)
        if runs[workflow] is None:
            summary(f"Waiting for a successful `{workflow}` run on master at `{args.sha}`. No release published.")
            return
    with tempfile.TemporaryDirectory(prefix="gitextensions-release-") as temporary:
        root = Path(temporary)
        assets = []
        for workflow, name, runtime, pattern in DELIVERIES:
            pages = json.loads(gh("api", "--paginate", "--slurp", f"{prefix}/actions/runs/{runs[workflow]['id']}/artifacts?per_page=100",
                                  capture_output=True, text=True).stdout)
            candidates = [artifact for page in pages for artifact in page["artifacts"] if artifact["name"] == name and not artifact["expired"]]
            if not candidates:
                raise ValueError(f"Missing verified artifact: {name}")
            artifact = max(candidates, key=lambda candidate: candidate["id"])
            archive = root / (name + ".artifact.zip")
            with archive.open("wb") as output:
                gh("api", "--allow-escape-sequences", f"{prefix}/actions/artifacts/{artifact['id']}/zip", stdout=output)
            assets.append(extract_delivery(archive, artifact, root, pattern, runtime, args.sha))
        checksums = root / "SHA256SUMS"
        checksums.write_text("".join(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n" for path in assets), encoding="utf-8")
        assets.append(checksums)
        notes = root / "release-notes.md"
        links = "\n".join(f"- [{workflow}]({run['html_url']})" for workflow, run in runs.items())
        notes.write_text(f"""Alpha builds of Git Extensions for macOS and Linux, from [`{args.sha[:12]}`](https://github.com/{args.repository}/commit/{args.sha}).

Download the `.zip` matching your Mac (Apple Silicon: `osx-arm64`, Intel: `osx-x64`), or the `.deb` for Debian/Ubuntu amd64. The packages include .NET; install Git separately.

On macOS 15+, extract the ZIP and move `Git Extensions.app` into Applications. These alpha bundles are ad-hoc signed and are not notarized; macOS may require approval in System Settings > Privacy & Security.

On Linux, install the package with `sudo apt install ./gitextensions_*_amd64.deb`. `SHA256SUMS` contains the download checksums.

These are the exact packages verified by the following successful CI runs, with no release rebuild:
{links}
""", encoding="utf-8")
        if args.dry_run:
            summary("Verified release assets: " + ", ".join(path.name for path in assets))
        else:
            summary(f"[Download the macOS / Linux prerelease]({publish(args.repository, args.sha, assets, notes)})")


if __name__ == "__main__":
    main()
