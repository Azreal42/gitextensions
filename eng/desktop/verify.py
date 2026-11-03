#!/usr/bin/env python3
"""Verify the extracted delivery and execute its real native desktop backend."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import signal
import subprocess
import tempfile
import zipfile

from package import machine, run


def desktop_smoke(arguments, environment, results, mac):
    child = subprocess.Popen(arguments, env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                             text=True, encoding="utf-8", errors="replace", start_new_session=mac)
    try:
        output, error = child.communicate(timeout=90)
    except subprocess.TimeoutExpired as timeout:
        # Preserve the still-live process identity and its output before cleanup.
        snapshot = {"pid": child.pid, "parent_exit_before_cleanup": child.poll(), "timed_out": True}
        (results / "desktop-timeout.json").write_text(json.dumps(snapshot, indent=2), encoding="utf-8")
        for name, value in [("stdout", timeout.output), ("stderr", timeout.stderr)]:
            if isinstance(value, bytes):
                value = value.decode("utf-8", errors="replace")
            (results / ("desktop." + name + ".log")).write_text(value or "", encoding="utf-8")
        if mac:
            try:
                os.killpg(child.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        elif child.poll() is None:
            cleanup = subprocess.run(["taskkill", "/PID", str(child.pid), "/T", "/F"], capture_output=True,
                                     text=True, errors="replace", timeout=20)
            (results / "desktop-cleanup.log").write_text(cleanup.stdout + cleanup.stderr, encoding="utf-8")
        try:
            output, error = child.communicate(timeout=20)
            snapshot["parent_exit_after_cleanup"] = child.returncode
            (results / "desktop.stdout.log").write_text(output, encoding="utf-8")
            (results / "desktop.stderr.log").write_text(error, encoding="utf-8")
        finally:
            (results / "desktop-timeout.json").write_text(json.dumps(snapshot, indent=2), encoding="utf-8")
        raise RuntimeError("Native desktop timed out; process identity and output were preserved before cleanup") from timeout
    (results / "desktop.stdout.log").write_text(output, encoding="utf-8")
    (results / "desktop.stderr.log").write_text(error, encoding="utf-8")
    if child.returncode != 0 or "Desktop smoke test passed" not in output:
        raise RuntimeError(f"Native desktop failed ({child.returncode}): {output}\n{error}")
    return child.returncode


def verify(args):
    results = args.results.resolve()
    results.mkdir(parents=True, exist_ok=True)
    archives = list(args.directory.glob("gitextensions_*.zip"))
    if len(archives) != 1:
        raise ValueError("Expected exactly one delivery archive")
    mac = args.runtime.startswith("osx-")
    if mac != (platform.system() == "Darwin") or (args.runtime.startswith("win-") and platform.system() != "Windows"):
        raise RuntimeError("The delivery must be exercised on its actual native operating system")
    with tempfile.TemporaryDirectory(prefix="gitextensions native delivery ") as temporary:
        root = Path(temporary)
        extracted = root / "extracted"
        extracted.mkdir()
        if mac:
            run(["ditto", "-x", "-k", archives[0].resolve(), extracted])
        else:
            with zipfile.ZipFile(archives[0]) as archive:
                archive.extractall(extracted)
        manifest = json.loads((extracted / "delivery-manifest.json").read_text(encoding="utf-8"))
        if manifest["runtime"] != args.runtime:
            raise ValueError("Wrong delivery runtime")
        for name, digest in manifest["files"].items():
            path = extracted / name
            if not path.resolve().is_relative_to(extracted.resolve()) or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
                raise ValueError("Extracted payload differs: " + name)
        bundle = extracted / ("Git Extensions.app" if mac else "GitExtensions")
        payload = bundle / "Contents/MacOS" if mac else bundle
        executable = payload / ("GitExtensions" if mac else "GitExtensions.exe")
        if machine(executable) != args.runtime:
            raise ValueError("Wrong native apphost architecture")
        if mac:
            run(["codesign", "--verify", "--deep", "--strict", "--verbose=2", bundle])
        environment = os.environ.copy()
        for name, child in [("XDG_CONFIG_HOME", "config"), ("XDG_CACHE_HOME", "cache"), ("XDG_DATA_HOME", "data")]:
            environment[name] = str(root / child)
        empty = root / "empty.gitconfig"
        empty.write_text("", encoding="utf-8")
        environment["GIT_CONFIG_GLOBAL"] = str(empty)
        environment["GIT_CONFIG_NOSYSTEM"] = "1"
        version = subprocess.run([str(executable), "--version"], env=environment, capture_output=True, text=True, encoding="utf-8", timeout=30, check=True)
        if "Git Extensions " + manifest["version"] not in version.stdout:
            raise ValueError("Wrong application version: " + version.stdout + version.stderr)
        helper = payload / ("gitextensions-askpass" if mac else "gitextensions-askpass.exe")
        for arguments in [[], ["invalid first", "invalid second"]]:
            response = subprocess.run([str(helper), *arguments], env=environment, capture_output=True, timeout=30)
            if response.returncode != 1 or response.stdout or response.stderr:
                raise ValueError("Askpass bad-arity protocol failed")
        repository = root / "repository space λ"
        run(["git", "init", "--initial-branch=main", repository], env=environment)
        (repository / "file λ.txt").write_text("Native Avalonia workflow\n", encoding="utf-8")
        run(["git", "-C", repository, "add", "--", "file λ.txt"], env=environment)
        run(["git", "-C", repository, "-c", "user.name=Platform Test", "-c", "user.email=platform@example.invalid",
             "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + str(root / "no-hooks"), "commit", "-m", "Native delivery smoke"], env=environment)
        environment["GITEXTENSIONS_SMOKE_SCREENSHOT"] = str(results / "native-desktop.png")
        smoke_exit = desktop_smoke([str(executable), "--smoke-test", str(repository)], environment, results, mac)
        if not (results / "native-desktop.png").is_file():
            raise RuntimeError("The native desktop did not produce its requested rendered frame")
        receipt = {"runtime": args.runtime, "source_commit": manifest["source_commit"], "version": manifest["version"],
                   "platform": platform.platform(), "payload_files_verified": len(manifest["files"]),
                   "native_backend_started": True, "repository_loaded": True, "desktop_exit": smoke_exit,
                   "archive_sha256": hashlib.sha256(archives[0].read_bytes()).hexdigest(), "signing": manifest["signing"],
                   "notarized": manifest["notarized"]}
        (results / "native-delivery-review.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(receipt, indent=2), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", choices=["win-x64", "win-arm64", "osx-x64", "osx-arm64"], required=True)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--results", type=Path, required=True)
    verify(parser.parse_args())
