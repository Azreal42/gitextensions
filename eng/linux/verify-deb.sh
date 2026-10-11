#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

fail() { printf '%s\n' "$*" >&2; exit 1; }
runtime_checks=true
case "${1:-}" in
    --no-runtime) runtime_checks=false; shift ;;
    --help|-h)
        printf '%s\n' 'Usage: eng/linux/verify-deb.sh [--no-runtime] PACKAGE.deb' \
            'Extracts and verifies without root. GUI checks use DISPLAY or xvfb-run.'
        exit 0 ;;
esac
(($# == 1)) || fail "Provide exactly one .deb file"
package=$(readlink -f -- "$1")
[[ -f "$package" ]] || fail "Package not found: $package"
[[ $(dpkg-deb --field "$package" Package) == gitextensions ]] || fail "Unexpected package name"
architecture=$(dpkg-deb --field "$package" Architecture)
[[ "$architecture" == amd64 || "$architecture" == arm64 ]] || fail "Unexpected architecture: $architecture"
dpkg-deb --fsys-tarfile "$package" | tar --numeric-owner -tvf - | \
    awk '$2 != "0/0" { bad = 1 } END { exit bad }' || fail "Package files must be owned by root"
work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT
dpkg-deb --extract "$package" "$work_dir/root"
dpkg-deb --control "$package" "$work_dir/control"
for name in preinst postinst prerm postrm config triggers; do
    [[ ! -e "$work_dir/control/$name" ]] || fail "Unexpected package installation hook: $name"
done
(
    cd "$work_dir/root"
    md5sum --quiet --check "$work_dir/control/md5sums"
)
root="$work_dir/root"
for path in usr/bin/gitextensions usr/lib/gitextensions/GitExtensions usr/lib/gitextensions/gitextensions-askpass \
    usr/lib/gitextensions/libcoreclr.so usr/share/applications/gitextensions.desktop \
    usr/share/metainfo/gitextensions.metainfo.xml usr/share/doc/gitextensions/copyright \
    usr/share/doc/gitextensions/third-party/PACKAGES.txt \
    usr/share/icons/hicolor/256x256/apps/gitextensions.png usr/share/man/man1/gitextensions.1.gz; do
    [[ -f "$root/$path" ]] || fail "Package is missing $path"
done
[[ -x "$root/usr/bin/gitextensions" && -x "$root/usr/lib/gitextensions/GitExtensions" \
    && -x "$root/usr/lib/gitextensions/gitextensions-askpass" ]] || fail "Launchers are not executable"
sh -n "$root/usr/lib/gitextensions/gitextensions-askpass"
gzip -t "$root/usr/share/man/man1/gitextensions.1.gz"
if command -v desktop-file-validate >/dev/null; then
    desktop-file-validate "$root/usr/share/applications/gitextensions.desktop"
fi
if command -v appstreamcli >/dev/null; then
    appstreamcli validate --no-net "$root/usr/share/metainfo/gitextensions.metainfo.xml"
fi
if "$runtime_checks"; then
    [[ "$architecture" == "$(dpkg --print-architecture)" ]] || fail "Cross-architecture package: use --no-runtime"
    if timeout 5s env -u DISPLAY -u WAYLAND_DISPLAY "$root/usr/lib/gitextensions/gitextensions-askpass" \
        invalid-first invalid-second > "$work_dir/askpass.stdout" 2> "$work_dir/askpass.stderr"; then
        fail "Askpass accepted two arguments"
    else
        askpass_exit=$?
        [[ "$askpass_exit" == 1 ]] || fail "Unexpected askpass bad-arity exit: $askpass_exit"
    fi
    [[ ! -s "$work_dir/askpass.stdout" && ! -s "$work_dir/askpass.stderr" ]] \
        || fail "Askpass bad-arity protocol emitted unexpected output"
    version_output=$("$root/usr/bin/gitextensions" --version)
    [[ "$version_output" == "Git Extensions "* ]] || fail "Unexpected application version output: $version_output"
    printf '%s\n' "$version_output"
    "$root/usr/bin/gitextensions" --help
    repository="$work_dir/smoke repository"
    git init --quiet "$repository"
    git -C "$repository" -c user.name=PackageTest -c user.email=package-test@example.invalid \
        -c commit.gpgsign=false -c core.hooksPath="$work_dir/no-hooks" \
        commit --quiet --allow-empty -m 'Package smoke test'
    export XDG_CONFIG_HOME="$work_dir/config" XDG_CACHE_HOME="$work_dir/cache" XDG_DATA_HOME="$work_dir/data"
    export LIBGL_ALWAYS_SOFTWARE=1
    if [[ -n "${DISPLAY:-}" ]]; then
        timeout 45s "$root/usr/bin/gitextensions" --smoke-test "$repository"
    elif command -v xvfb-run >/dev/null; then
        timeout 45s xvfb-run -a "$root/usr/bin/gitextensions" --smoke-test "$repository"
    else
        fail "GUI verification requires DISPLAY or xvfb-run (install xvfb and xauth)"
    fi
fi
printf 'Verified: %s\n' "$package"
