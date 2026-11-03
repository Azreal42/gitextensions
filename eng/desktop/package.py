#!/usr/bin/env python3
"""Build self-contained Avalonia deliveries for native Windows/macOS runners."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile


def run(arguments, **kwargs):
    print("+", " ".join(map(str, arguments)), flush=True)
    return subprocess.run(list(map(str, arguments)), check=True, **kwargs)


def machine(path):
    with path.open("rb") as stream:
        header = stream.read(64)
        if header[:2] == b"MZ":
            stream.seek(struct.unpack_from("<I", header, 60)[0])
            signature = stream.read(6)
            if signature[:4] != b"PE\0\0":
                raise ValueError("Invalid PE executable")
            return {0x8664: "win-x64", 0xAA64: "win-arm64"}.get(struct.unpack_from("<H", signature, 4)[0])
        if header[:4] == b"\xcf\xfa\xed\xfe":
            return {0x1000007: "osx-x64", 0x100000C: "osx-arm64"}.get(struct.unpack_from("<I", header, 4)[0])
    raise ValueError(f"Unsupported native executable: {path}")


def create_icon(repository, resources):
    source = repository / "setup/assets/Logo/git-extensions-logo-512px.png"
    with tempfile.TemporaryDirectory(prefix="gitextensions-icon-") as temporary:
        iconset = Path(temporary) / "GitExtensions.iconset"
        iconset.mkdir()
        for size in [16, 32, 128, 256, 512]:
            for scale in [1, 2]:
                if size * scale > 512:
                    continue
                name = f"icon_{size}x{size}" + ("@2x" if scale == 2 else "") + ".png"
                run(["sips", "-z", size * scale, size * scale, source, "--out", iconset / name], stdout=subprocess.DEVNULL)
        run(["iconutil", "-c", "icns", iconset, "-o", resources / "GitExtensions.icns"])


def sign_bundle(bundle, identity, repository):
    entitlements = repository / "setup/macos/GitExtensions.entitlements"
    native = bundle / "Contents/MacOS"
    for path in sorted(native.rglob("*")):
        if not path.is_file():
            continue
        # Contents/MacOS is a code location: codesign also requires signatures
        # for the managed/runtime data and notices placed beside the apphost.
        # Sign every nested file before sealing the main executable's bundle.
        if path.name == "GitExtensions":
            continue
        command = ["codesign", "--force", "--sign", identity]
        if identity != "-":
            command.extend(["--timestamp", "--options=runtime"])
        run([*command, path])
    command = ["codesign", "--force", "--sign", identity]
    if identity != "-":
        command.extend(["--timestamp", "--options=runtime", "--entitlements", entitlements])
    run([*command, bundle])
    run(["codesign", "--verify", "--strict", "--verbose=2", bundle])


def build(args):
    repository = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    stage = output / "stage"
    if stage.exists():
        raise FileExistsError(f"Refusing to overwrite an existing delivery stage: {stage}")
    output.mkdir(parents=True, exist_ok=True)
    version = args.version
    if version is None:
        value = subprocess.check_output([args.dotnet, "msbuild", str(repository / "src/linux/GitExtensions.Desktop/GitExtensions.Desktop.csproj"),
                                         "-nologo", "-getProperty:Version"], cwd=repository, text=True).strip()
        version = value.split("-", 1)[0] + "-avalonia-alpha"
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?", version):
        raise ValueError("Invalid application version")
    numeric_version = version.split("-", 1)[0]
    mac = args.runtime.startswith("osx-")
    if mac and sys.platform != "darwin":
        raise RuntimeError("Create and sign macOS bundles on a native macOS runner")
    bundle = stage / "Git Extensions.app" if mac else stage / "GitExtensions"
    payload = bundle / "Contents/MacOS" if mac else bundle
    payload.mkdir(parents=True)
    publish = ["-c", "Release", "-r", args.runtime, "--self-contained", "true", "--output", payload,
               "--disable-build-servers", "-m:1", f"-p:Version={version}", "-p:ContinuousIntegrationBuild=true",
               "-p:UseSharedCompilation=false", "-p:PublishSingleFile=false", "-p:PublishTrimmed=false"]
    run([args.dotnet, "publish", repository / "src/linux/GitExtensions.Desktop/GitExtensions.Desktop.csproj", *publish], cwd=repository)
    if not mac:
        run([args.dotnet, "publish", repository / "src/linux/GitExtensions.Askpass.Host/GitExtensions.Askpass.Host.csproj", *publish], cwd=repository)
    executable = payload / ("GitExtensions" if mac else "GitExtensions.exe")
    if machine(executable) != args.runtime:
        raise ValueError("The published apphost does not match the delivery runtime")
    required = ["GitExtensions.dll", "GitExtensions.deps.json", "GitExtensions.runtimeconfig.json",
                "libcoreclr.dylib" if mac else "coreclr.dll", "gitextensions-askpass" if mac else "gitextensions-askpass.exe"]
    for name in required:
        if not (payload / name).is_file():
            raise FileNotFoundError(f"Incomplete self-contained delivery: {name}")
    if (payload / "gitextensions-exec").exists():
        raise ValueError("A Linux-only process launcher must not enter a Windows/macOS delivery")
    if not mac and machine(payload / "gitextensions-askpass.exe") != args.runtime:
        raise ValueError("The native askpass host does not match the delivery runtime")
    notices = payload / "third-party-notices"
    cache = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
    run([sys.executable, repository / "eng/linux/collect-package-notices.py", payload / "GitExtensions.deps.json", cache, notices])
    shutil.copy2(repository / "LICENSE.md", payload / "LICENSE.md")
    local_notices = notices / "GitExtensions.Avalonia.Skia.Patched"
    local_notices.mkdir()
    for name in ["LICENSE.txt", "NOTICE.md"]:
        shutil.copy2(repository / "src/linux/GitExtensions.Avalonia.Skia.Patched" / name, local_notices / name)
    if mac:
        resources = bundle / "Contents/Resources"
        resources.mkdir()
        create_icon(repository, resources)
        info = {"CFBundleName": "Git Extensions", "CFBundleDisplayName": "Git Extensions", "CFBundleExecutable": "GitExtensions",
                "CFBundleIdentifier": "io.github.gitextensions.avalonia", "CFBundlePackageType": "APPL",
                "CFBundleIconFile": "GitExtensions.icns", "CFBundleShortVersionString": numeric_version,
                "CFBundleVersion": numeric_version, "CFBundleGetInfoString": version,
                "LSMinimumSystemVersion": "15.0", "NSHighResolutionCapable": True}
        with (bundle / "Contents/Info.plist").open("wb") as stream:
            plistlib.dump(info, stream)
        (bundle / "Contents/PkgInfo").write_bytes(b"APPL????")
        executable.chmod(0o755)
        (payload / "gitextensions-askpass").chmod(0o755)
        sign_bundle(bundle, args.signing_identity, repository)
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository, text=True).strip()
    files = {path.relative_to(stage).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
             for path in sorted(stage.rglob("*")) if path.is_file()}
    manifest = {"runtime": args.runtime, "version": version, "source_commit": source,
                "signing": "ad-hoc" if mac and args.signing_identity == "-" else "developer-id" if mac else "unsigned",
                "notarized": False, "files": files}
    (stage / "delivery-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    archive = output / f"gitextensions_{version}_{args.runtime}.zip"
    if mac:
        run(["ditto", "-c", "-k", "--sequesterRsrc", stage, archive])
    else:
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as result:
            for path in sorted(stage.rglob("*")):
                if path.is_file():
                    result.write(path, path.relative_to(stage))
    print("Delivery: " + str(archive), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", choices=["win-x64", "win-arm64", "osx-x64", "osx-arm64"], required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version")
    parser.add_argument("--dotnet", default=os.environ.get("DOTNET", "dotnet"))
    parser.add_argument("--signing-identity", default="-")
    build(parser.parse_args())
