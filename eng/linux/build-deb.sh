#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C
umask 022

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
project="$repo_root/src/linux/GitExtensions.Desktop/GitExtensions.Desktop.csproj"
runtime=linux-x64
version=
output_dir="$repo_root/artifacts/linux/deb"
publish_dir=

usage() {
    cat <<'USAGE'
Usage: eng/linux/build-deb.sh [options]
  --runtime linux-x64|linux-arm64  Target architecture (default: linux-x64)
  --version VERSION               .NET version (default: project Version)
  --output DIRECTORY              Package destination (default: artifacts/linux/deb)
  --publish-directory DIRECTORY   Package an existing self-contained publish;
                                  requires --version and skips compilation
  --help                          Show this help
Environment: DOTNET, SOURCE_DATE_EPOCH, DEB_MAINTAINER, NUGET_PACKAGES, DOTNET_CLI_HOME
The build needs no root privileges and does not install packages.
USAGE
}

fail() { printf '%s\n' "$*" >&2; exit 1; }
while (($#)); do
    case "$1" in
        --runtime|--version|--output|--publish-directory)
            (($# >= 2)) || fail "Missing value for $1"
            case "$1" in
                --runtime) runtime=$2 ;;
                --version) version=$2 ;;
                --output) output_dir=$2 ;;
                --publish-directory) publish_dir=$2 ;;
            esac
            shift 2 ;;
        --help|-h) usage; exit 0 ;;
        *) fail "Unknown argument: $1" ;;
    esac
done
case "$runtime" in
    linux-x64) architecture=amd64; elf_machine=62 ;;
    linux-arm64) architecture=arm64; elf_machine=183 ;;
    *) fail "Unsupported runtime: $runtime" ;;
esac
for tool in dpkg dpkg-deb git gzip find install md5sum od python3 readelf sh sort touch; do
    command -v "$tool" >/dev/null || fail "Required tool not found: $tool"
done

export SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-$(git -C "$repo_root" log -1 --format=%ct)}
[[ "$SOURCE_DATE_EPOCH" =~ ^[0-9]+$ ]] || fail "SOURCE_DATE_EPOCH must be an integer"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_CLI_HOME=${DOTNET_CLI_HOME:-"$repo_root/artifacts/dotnet-home"}
export NUGET_PACKAGES=${NUGET_PACKAGES:-"$repo_root/artifacts/nuget"}

mkdir -p -- "$output_dir"
output_dir=$(cd -- "$output_dir" && pwd)
work_dir=$(mktemp -d "$output_dir/.deb-build.XXXXXXXX")
trap 'rm -rf -- "$work_dir"' EXIT

if [[ -z "$publish_dir" ]]; then
    if [[ -z "${DOTNET:-}" ]]; then
        if [[ -x "$repo_root/.dotnet/dotnet" ]]; then
            DOTNET="$repo_root/.dotnet/dotnet"
        else
            DOTNET=$(command -v dotnet) || fail "Install the .NET 10 SDK or set DOTNET"
        fi
    fi
    [[ -x "$DOTNET" ]] || fail "DOTNET must name an executable: $DOTNET"
    [[ -f "$project" ]] || fail "Linux desktop project not found: $project"
    if [[ -z "$version" ]]; then
        version=$("$DOTNET" msbuild "$project" -nologo -getProperty:Version)
    fi
    publish_dir="$work_dir/publish"
    "$DOTNET" publish "$project" --configuration Release --runtime "$runtime" \
        --self-contained true --output "$publish_dir" --nologo -m:1 \
        -p:Version="$version" -p:ContinuousIntegrationBuild=true \
        -p:PublishSingleFile=false -p:PublishTrimmed=false
else
    [[ -n "$version" ]] || fail "--publish-directory requires --version"
    [[ -d "$publish_dir" ]] || fail "Publish directory not found: $publish_dir"
    publish_dir=$(cd -- "$publish_dir" && pwd)
fi

# NuGet prereleases sort before final versions in Debian as well.
deb_version=${version/-/"~"}
dpkg --validate-version "$deb_version" || fail "Invalid package version: $deb_version"
[[ "$deb_version" != *:* ]] || fail "Version epochs are not supported in .NET versions"
for name in GitExtensions GitExtensions.dll GitExtensions.deps.json GitExtensions.runtimeconfig.json libcoreclr.so gitextensions-askpass; do
    [[ -f "$publish_dir/$name" ]] || fail "Self-contained publish is missing $name"
done
sh -n "$publish_dir/gitextensions-askpass"
elf_header=$(od -An -tx1 -N6 "$publish_dir/GitExtensions" | tr -d ' \n')
[[ "$elf_header" == 7f454c460201 ]] || fail "Published executable must be a 64-bit little-endian ELF"
machine=$(od -An -tu2 -j18 -N2 "$publish_dir/GitExtensions" | tr -d ' ')
[[ "$machine" == "$elf_machine" ]] || fail "Published executable does not match $runtime"
python3 "$repo_root/eng/linux/verify-native-runtime.py" --directory "$publish_dir" --runtime "$runtime" \
    --control "$repo_root/setup/linux/control.in"

stage="$work_dir/stage"
install -d "$stage/DEBIAN" "$stage/usr/lib/gitextensions" "$stage/usr/bin" \
    "$stage/usr/share/applications" "$stage/usr/share/metainfo" \
    "$stage/usr/share/doc/gitextensions" "$stage/usr/share/man/man1"
cp -a -- "$publish_dir/." "$stage/usr/lib/gitextensions/"
find "$stage/usr/lib/gitextensions" -type d -exec chmod 0755 {} +
find "$stage/usr/lib/gitextensions" -type f -perm /111 -exec chmod 0755 {} +
find "$stage/usr/lib/gitextensions" -type f ! -perm /111 -exec chmod 0644 {} +
chmod 0755 "$stage/usr/lib/gitextensions/GitExtensions" "$stage/usr/lib/gitextensions/gitextensions-askpass"
install -m 0755 "$repo_root/setup/linux/gitextensions" "$stage/usr/bin/gitextensions"
install -m 0644 "$repo_root/setup/linux/gitextensions.desktop" "$stage/usr/share/applications/"
install -m 0644 "$repo_root/setup/linux/gitextensions.metainfo.xml" "$stage/usr/share/metainfo/"
install -m 0644 "$repo_root/LICENSE.md" "$stage/usr/share/doc/gitextensions/copyright"
python3 "$repo_root/eng/linux/collect-package-notices.py" \
    "$publish_dir/GitExtensions.deps.json" "$NUGET_PACKAGES" \
    "$stage/usr/share/doc/gitextensions/third-party"
# This source-built backend is a project reference, not a NuGet license entry.
local_skia_notices="$stage/usr/share/doc/gitextensions/third-party/GitExtensions.Avalonia.Skia.Patched"
install -d "$local_skia_notices"
install -m 0644 "$repo_root/src/linux/GitExtensions.Avalonia.Skia.Patched/LICENSE.txt" "$local_skia_notices/LICENSE.txt"
install -m 0644 "$repo_root/src/linux/GitExtensions.Avalonia.Skia.Patched/NOTICE.md" "$local_skia_notices/NOTICE.md"
gzip -n -9 -c "$repo_root/setup/linux/gitextensions.1" > "$stage/usr/share/man/man1/gitextensions.1.gz"
for size in 16 24 32 48 64 128 256 512; do
    icon_dir="$stage/usr/share/icons/hicolor/${size}x${size}/apps"
    install -d "$icon_dir"
    install -m 0644 "$repo_root/setup/assets/Logo/git-extensions-logo-${size}px.png" "$icon_dir/gitextensions.png"
done
install -d "$stage/usr/share/icons/hicolor/scalable/apps" "$stage/usr/share/pixmaps"
install -m 0644 "$repo_root/setup/assets/Logo/git-extensions-logo.svg" "$stage/usr/share/icons/hicolor/scalable/apps/gitextensions.svg"
install -m 0644 "$repo_root/setup/assets/Logo/git-extensions-logo-256px.png" "$stage/usr/share/pixmaps/gitextensions.png"

maintainer=${DEB_MAINTAINER:-"Git Extensions contributors"}
[[ "$maintainer" != *$'\n'* && "$maintainer" != *$'\r'* ]] || fail "Invalid DEB_MAINTAINER"
installed_size=$(python3 "$repo_root/eng/linux/package-installed-size.py" "$stage/usr")
while IFS= read -r line; do
    line=${line//@VERSION@/$deb_version}
    line=${line//@ARCHITECTURE@/$architecture}
    line=${line//@MAINTAINER@/"$maintainer"}
    line=${line//@INSTALLED_SIZE@/$installed_size}
    printf '%s\n' "$line"
done < "$repo_root/setup/linux/control.in" > "$stage/DEBIAN/control"
(
    cd "$stage"
    find usr -type f -print0 | sort -z | xargs -0 md5sum > DEBIAN/md5sums
)
find "$stage" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
package="$output_dir/gitextensions_${deb_version}_${architecture}.deb"
dpkg-deb --root-owner-group --uniform-compression --threads-max=1 -Zxz -z9 --build "$stage" "$work_dir/package.deb"
mv -- "$work_dir/package.deb" "$package"
printf 'Package: %s\n' "$package"
