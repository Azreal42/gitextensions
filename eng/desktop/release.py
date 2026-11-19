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
    ("avalonia-platforms.yml", "avalonia-win-x64-installer", "win-x64", r"gitextensions_[0-9A-Za-z.-]+_win-x64-setup\.exe"),
)


def gh(*arguments, **kwargs):
    return subprocess.run(["gh", *map(str, arguments)], check=True, **kwargs)


def api(path):
    return json.loads(gh("api", path, capture_output=True, text=True).stdout)


def prerelease_tag(sha):
    return "avalonia-alpha-" + sha[:12]


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
        if runtime == "win-x64":
            receipt_name = "windows-installer-review.json"
            if len(files) != 2 or receipt_name not in outer.namelist():
                raise ValueError("Expected an installer and its Windows verification receipt.")
            receipt = json.loads(outer.read(receipt_name))
            files = [entry for entry in files if entry.filename != receipt_name]
        if len(files) != 1 or not re.fullmatch(pattern, files[0].filename):
            raise ValueError("Expected exactly one delivery with its original platform filename.")
        # GitHub rewrites characters such as Debian's '~' during upload. Choose
        # the final download name before generating checksums, preserving bytes.
        target = destination / re.sub(r"[^0-9A-Za-z._-]", ".", files[0].filename)
        with outer.open(files[0]) as source, target.open("wb") as output:
            shutil.copyfileobj(source, output)
    if runtime.startswith("osx-"):
        with zipfile.ZipFile(target) as inner:
            manifest = json.loads(inner.read("delivery-manifest.json"))
        if manifest["source_commit"] != sha or manifest["runtime"] != runtime:
            raise ValueError("The native delivery does not belong to the validated commit and platform.")
    elif runtime == "win-x64":
        if (receipt["source_commit"] != sha or receipt["runtime"] != runtime
                or receipt["installer_sha256"] != hashlib.sha256(target.read_bytes()).hexdigest()):
            raise ValueError("The Windows installer does not match the one exercised on the validated commit.")
    return target


def optional_api(path):
    result = subprocess.run(["gh", "api", path], capture_output=True, text=True)
    if result.returncode == 0:
        return json.loads(result.stdout)
    if "HTTP 404" not in result.stderr:
        result.check_returncode()
    return None


def release_by_tag(repository, tag):
    release = optional_api(f"repos/{repository}/releases/tags/{tag}")
    if release is not None:
        return release
    # The tag endpoint cannot find a draft whose Git tag has not been created.
    # Listing releases includes drafts when authenticated with push permission.
    pages = json.loads(gh("api", "--paginate", "--slurp", f"repos/{repository}/releases?per_page=100",
                          capture_output=True, text=True).stdout)
    return next((release for page in pages for release in page if release["tag_name"] == tag), None)


def verify_release(release, sha, assets):
    if release["target_commitish"] != sha:
        raise ValueError("The release tag is already assigned to another source.")
    expected = {path.name: "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest() for path in assets}
    actual = {asset["name"]: asset.get("digest") for asset in release["assets"]}
    if actual != expected:
        raise ValueError("The uploaded release assets do not match the verified packages.")


def publish(repository, sha, assets, notes, release_tag=None):
    tag = release_tag or prerelease_tag(sha)
    prerelease = not release_tag
    flags = ["--prerelease", "--latest=false"] if prerelease else ["--prerelease=false", "--latest"]
    title = f"Git Extensions Desktop alpha ({sha[:8]})" if prerelease else f"Git Extensions Desktop {tag.removeprefix('desktop-')}"
    release = release_by_tag(repository, tag)
    if release is not None and release["target_commitish"] != sha:
        raise ValueError("Refusing to replace a tag assigned to another commit.")
    if release is not None and not release["draft"]:
        # A rerun can produce a different ZIP timestamp for the same source.
        # Keep the already verified, published delivery instead of replacing it.
        if {asset["name"] for asset in release["assets"]} != {path.name for path in assets}:
            raise ValueError("The published release is missing expected delivery assets.")
        if release_tag and release["prerelease"]:
            raise ValueError("This version is already in use by a prerelease.")
        return release["html_url"]
    if release_tag:
        # GitHub ignores --target when a tag already exists. Never attach these
        # binaries to an unrelated tag, including a tag without a release page.
        ref = optional_api(f"repos/{repository}/git/ref/tags/{tag}")
        if ref is not None:
            target = ref["object"]
            while target["type"] == "tag":
                target = api(f"repos/{repository}/git/tags/{target['sha']}")["object"]
            if target["type"] != "commit" or target["sha"] != sha:
                raise ValueError("The version tag already points to another source.")
    if release is None:
        gh("release", "create", tag, "--repo", repository, "--target", sha, "--draft",
           "--title", title, "--notes-file", notes)
    gh("release", "upload", tag, *assets, "--repo", repository, "--clobber")
    verify_release(release_by_tag(repository, tag), sha, assets)
    gh("release", "edit", tag, "--repo", repository, "--draft=false", *flags)
    release = release_by_tag(repository, tag)
    verify_release(release, sha, assets)
    if release["draft"] or release["prerelease"] != prerelease:
        raise ValueError("The requested release was not published.")
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
    parser.add_argument("--release-tag", help="Publish a numbered Latest release, for example desktop-7.2.1")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    if args.release_tag and not re.fullmatch(r"desktop-\d+\.\d+\.\d+(?:\.\d+)?", args.release_tag, re.ASCII):
        parser.error("Expected a numbered desktop release tag, for example desktop-7.2.1.")
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
            if args.release_tag:
                raise ValueError(f"A successful {workflow} run on master at {args.sha} is required for a numbered release.")
            summary(f"Waiting for a successful `{workflow}` run on master at `{args.sha}`. No release published.")
            return
    with tempfile.TemporaryDirectory(prefix="gitextensions-release-") as temporary:
        root = Path(temporary)
        packages = {}
        for workflow, name, runtime, pattern in DELIVERIES:
            pages = json.loads(gh("api", "--paginate", "--slurp", f"{prefix}/actions/runs/{runs[workflow]['id']}/artifacts?per_page=100",
                                  capture_output=True, text=True).stdout)
            candidates = [artifact for page in pages for artifact in page["artifacts"] if artifact["name"] == name and not artifact["expired"]]
            if not candidates:
                if runtime == "win-x64" and not args.release_tag:
                    # Historical prereleases predate installer builds. Leave a
                    # complete public release intact when rerunning its old CI.
                    previous = release_by_tag(args.repository, prerelease_tag(args.sha))
                    if (previous and not previous["draft"] and previous["target_commitish"] == args.sha
                            and {a["name"] for a in previous["assets"]} == {p.name for p in packages.values()} | {"SHA256SUMS"}):
                        summary(f"Existing macOS / Linux release: {previous['html_url']}")
                        return
                raise ValueError(f"Missing verified artifact: {name}")
            artifact = max(candidates, key=lambda candidate: candidate["id"])
            archive = root / (name + ".artifact.zip")
            with archive.open("wb") as output:
                gh("api", "--allow-escape-sequences", f"{prefix}/actions/artifacts/{artifact['id']}/zip", stdout=output)
            packages[runtime] = extract_delivery(archive, artifact, root, pattern, runtime, args.sha)
        assets = list(packages.values())
        checksums = root / "SHA256SUMS"
        checksums.write_text("".join(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n" for path in assets), encoding="utf-8")
        assets.append(checksums)
        notes = root / "release-notes.md"
        links = "\n".join(f"- [{workflow}]({run['html_url']})" for workflow, run in runs.items())
        download = f"https://github.com/{args.repository}/releases/download/{args.release_tag or prerelease_tag(args.sha)}"
        notes.write_text(f"""Git Extensions Desktop for Windows, macOS and Linux, built from [`{args.sha[:12]}`](https://github.com/{args.repository}/commit/{args.sha}). These packages contain the Avalonia edition, currently in alpha. They include .NET. Install Git separately.

- [Windows x64 installer (.exe)]({download}/{packages['win-x64'].name})
- [macOS Apple Silicon (.zip)]({download}/{packages['osx-arm64'].name})
- [macOS Intel (.zip)]({download}/{packages['osx-x64'].name})
- [Debian / Ubuntu amd64 (.deb)]({download}/{packages['linux-amd64'].name})
- [SHA-256 checksums]({download}/SHA256SUMS)

On Windows, run the installer and open Git Extensions (Avalonia) from the Start menu. It installs for the current user. The installer is unsigned.

On macOS 15+, choose `osx-arm64` for Apple Silicon or `osx-x64` for Intel. Extract the ZIP and move `Git Extensions.app` into Applications. These alpha bundles are ad-hoc signed and are not notarized. macOS may require approval in System Settings > Privacy & Security.

On Linux, install the package with `sudo apt install ./gitextensions_*_amd64.deb`. `SHA256SUMS` contains the download checksums.

These are the exact packages verified by the following successful CI runs, with no release rebuild:
{links}
""", encoding="utf-8")
        if args.dry_run:
            summary("Verified release assets: " + ", ".join(path.name for path in assets))
        else:
            summary(f"[Download Git Extensions Desktop]({publish(args.repository, args.sha, assets, notes, args.release_tag)})")


if __name__ == "__main__":
    main()
