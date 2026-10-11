#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

# Mirror host dependencies in an isolated database, install into a temporary root,
# and run the installed application against the host's native libraries.
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
fail() { printf '%s\n' "$*" >&2; exit 1; }
case "${1:-}" in
    --help|-h)
        printf '%s\n' 'Usage: eng/linux/verify-installed-deb.sh PACKAGE.deb' \
            'Requires fakeroot and DISPLAY or xvfb-run. Leaves the system package database unchanged.'
        exit 0 ;;
esac
(($# == 1)) || fail "Provide exactly one .deb file"
command -v fakeroot >/dev/null || fail "Install fakeroot to verify an isolated package installation"
package=$(readlink -f -- "$1")
[[ -f "$package" ]] || fail "Package not found: $package"
"$repo_root/eng/linux/verify-deb.sh" --no-runtime "$package"
mkdir -p "$repo_root/artifacts/linux"
work_dir=$(mktemp -d "$repo_root/artifacts/linux/.install-check.XXXXXXXX")
trap 'rm -rf -- "$work_dir"' EXIT
install_root="$work_dir/root"
install -d "$install_root/var/lib/dpkg/info" "$install_root/var/log"
cp /var/lib/dpkg/status "$install_root/var/lib/dpkg/status"
# Copy only installed-file metadata: no maintainer scripts, hooks or triggers.
find /var/lib/dpkg/info -maxdepth 1 -type f -name '*.list' \
    -exec cp --target-directory="$install_root/var/lib/dpkg/info" -- {} +
fakeroot dpkg --root "$install_root" --log "$install_root/var/log/dpkg.log" \
    --force-bad-path --no-triggers --install "$package"
status=$(dpkg-query --admindir "$install_root/var/lib/dpkg" --show --showformat='${Status}' gitextensions)
[[ "$status" == 'install ok installed' ]] || fail "Package was not configured successfully: $status"
"$install_root/usr/bin/gitextensions" --version
repository="$work_dir/installed smoke repository"
git init --quiet "$repository"
git -C "$repository" -c user.name=PackageTest -c user.email=package-test@example.invalid \
    -c commit.gpgsign=false -c core.hooksPath="$work_dir/no-hooks" \
    commit --quiet --allow-empty -m 'Installed package smoke test'
export XDG_CONFIG_HOME="$work_dir/config" XDG_CACHE_HOME="$work_dir/cache" XDG_DATA_HOME="$work_dir/data"
export LIBGL_ALWAYS_SOFTWARE=1
if [[ -n "${DISPLAY:-}" ]]; then
    timeout 45s "$install_root/usr/bin/gitextensions" --smoke-test "$repository"
elif command -v xvfb-run >/dev/null; then
    timeout 45s xvfb-run -a "$install_root/usr/bin/gitextensions" --smoke-test "$repository"
else
    fail "GUI verification requires DISPLAY or xvfb-run (install xvfb and xauth)"
fi
printf 'Installed and launched in an isolated root: %s\n' "$package"
