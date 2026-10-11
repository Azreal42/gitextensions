# Linux package

Build without root using the .NET 10 SDK, Git, Python 3 and `dpkg-deb`:

```sh
eng/linux/build-deb.sh
eng/linux/verify-deb.sh artifacts/linux/deb/gitextensions_*_amd64.deb
sudo apt install ./artifacts/linux/deb/gitextensions_*_amd64.deb
gitextensions /path/to/repository
```

`build-deb.sh --help` lists version, architecture and output options. Set `DOTNET`
to a specific SDK executable if needed. `SOURCE_DATE_EPOCH` controls timestamps;
by default it comes from the current Git commit. An identical published payload
and epoch produce identical packages. `DEB_MAINTAINER` sets the release contact.
The published dependency metadata and notices are copied from the NuGet cache.
`verify-installed-deb.sh` verifies installation and launch in an isolated dpkg
root using `fakeroot` and the host's dependency status.

Targets: Debian 12+ and Ubuntu 24.04+, amd64 or arm64, with Git 2.36+. The package includes .NET;
APT installs Git and native dependencies. The GUI needs X11 or XWayland. Install
`xvfb` and `xauth` for verification outside a desktop session.

Dependency sources: [Avalonia Linux deployment](https://docs.avaloniaui.net/docs/deployment/linux),
[.NET Debian](https://learn.microsoft.com/en-us/dotnet/core/install/linux-debian),
[.NET Ubuntu](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install).
