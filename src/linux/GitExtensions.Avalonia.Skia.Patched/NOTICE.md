# Avalonia.Skia local lifetime correction

This is a source-built Git Extensions fork of the Avalonia.Skia 11.3.22 backend,
not an unmodified vendor binary. The MIT license and upstream copyright are
in LICENSE.txt. All 54 C# preimages and the noise asset come from Avalonia commit
627ae9ef921621e27e7aa58df2796fbadb50af88; their checksums and source-package/PDB
correspondence are recorded in Provenance/. The original build checkout used
CRLF; these vendored Git blobs retain upstream LF bytes.

The only upstream code delta is `using var asset` in GlyphTypefaceImpl.TryGetStream.
It keeps the SKStreamAsset alive until the native read completes and disposes it
afterwards. No family, font style, cache policy, byte-copy behavior or native
dependency is changed. Historical short-read handling remains outside this patch.
Related upstream report: https://github.com/AvaloniaUI/Avalonia/issues/22323

The assembly intentionally retains the official Avalonia.Skia name, version and
public-key identity for existing friend-assembly access. It is public-signed with
the published public key; no private key is included. This is not an official
NuGet package signature or an endorsement by Avalonia. The project PackageId and
informational attribution distinguish this local fork. No primary NuGet cache is
modified. Restore/build/publish and behavioral validation are separate gates.
