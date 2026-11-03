using Avalonia.Headless.NUnit;
using Avalonia.Media;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[TestFixture, Category("CrossPlatform"), NonParallelizable]
public sealed class PlatformFontsTests
{
    [AvaloniaTest]
    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public void DefaultRolesResolveToAnInstalledSansOrFixedPitchFace(int roleValue)
    {
        DesktopFontRole role = (DesktopFontRole)roleValue;
        FontSpec request = new FontPreferences().For(role);
        ResolvedDesktopFont resolved = DesktopFontResolver.Resolve(request, role);
        Assert.That(resolved.Requested, Is.EqualTo(request), "Fallbacks must preserve the saved Windows family identity.");
        Assert.That(FontManager.Current.TryGetGlyphTypeface(new Typeface(resolved.Family, resolved.Style, resolved.Weight), out var glyph), Is.True);
        Assert.That(resolved.EffectiveFamilyName, Is.Not.Empty);
        if (role is DesktopFontRole.Code or DesktopFontRole.Monospace)
        {
            Assert.That(glyph!.Metrics.IsFixedPitch, Is.True);
            Assert.That(resolved.IsFixedPitch, Is.True);
        }
        else
        {
            Assert.That(resolved.Family.Name, Is.AnyOf("Segoe UI", "Noto Sans", "DejaVu Sans", "Helvetica Neue", "Arial"),
                "The compact browser must not fall back to an arbitrary serif family on a native host.");
            Assert.That(glyph!.Metrics.IsFixedPitch, Is.False);
        }
    }
}
