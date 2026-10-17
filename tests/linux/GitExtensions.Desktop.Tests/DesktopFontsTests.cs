using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class DesktopFontsTests
{
    [AvaloniaTest]
    public void ActiveSnapshotIsStableWhileAnIndependentSessionSeesSavedDefaults()
    {
        using Profile profile = new(); DesktopFontSession active = DesktopFonts.Active;
        FontPreferences saved = profile.Store.SaveChanges(profile.Store.Load(), new() { ShowEolMarkerAsGlyph = true, Code = new("Consolas", 12.25, true) });
        DesktopFonts.Initialize(); Assert.That(DesktopFonts.Active, Is.SameAs(active));
        Assert.That(DesktopFonts.Active.Preferences, Is.Not.EqualTo(saved)); Assert.That(DesktopFonts.ShowEolMarkerAsGlyph, Is.False);
        DesktopFontSession next = DesktopFonts.CreateSession(profile.Store);
        Assert.That(next.Preferences, Is.EqualTo(saved)); Assert.That(next.ShowEolMarkerAsGlyph, Is.True); Assert.That(next.LoadError, Is.Null);
    }

    [AvaloniaTest]
    public void CorruptPreferencesRemainExactAndProvideANonfatalAccessibleSessionError()
    {
        using Profile profile = new(); Directory.CreateDirectory(Path.GetDirectoryName(profile.Store.FilePath)!); File.WriteAllText(profile.Store.FilePath, "{invalid");
        byte[] bytes = File.ReadAllBytes(profile.Store.FilePath); DesktopFontSession session = DesktopFonts.CreateSession(profile.Store);
        Assert.That(session.LoadError, Is.InstanceOf<System.Text.Json.JsonException>()); Assert.That(session.Preferences, Is.EqualTo(new FontPreferences()));
        Assert.That(() => session.For(DesktopFontRole.Code), Throws.Nothing); Assert.That(File.ReadAllBytes(profile.Store.FilePath), Is.EqualTo(bytes));
    }

    [AvaloniaTest]
    public void MissingFamilyKeepsItsIdentityAndUsesAnExplicitActualFallback()
    {
        FontSpec requested = new("Missing literal family 9b30b127", 10.75, true, true);
        ResolvedDesktopFont font = DesktopFontResolver.Resolve(requested, DesktopFontRole.Code);
        Assert.That(font.Requested, Is.EqualTo(requested)); Assert.That(font.RequestedFamilyAvailable, Is.False);
        Assert.That(font.Diagnostic, Is.Not.Empty); Assert.That(font.EffectiveFamilyName, Is.Not.Empty); Assert.That(font.IsFixedPitch, Is.True);
        Assert.That(font.SizeInDips, Is.EqualTo(10.75 * 96 / 72).Within(1e-12));
        Assert.That(FontManager.Current.TryGetGlyphTypeface(new Typeface(font.Family, font.Style, font.Weight), out var glyph), Is.True);
        Assert.That(glyph!.Metrics.IsFixedPitch, Is.True);
    }

    [AvaloniaTest]
    public void CodeRejectsAProportionalFaceButOtherRolesRetainTheSameRealFace()
    {
        FontFamily proportional = FontManager.Current.SystemFonts.First(family => FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var glyph) && !glyph.Metrics.IsFixedPitch && !family.Name.StartsWith('@'));
        FontSpec requested = new(proportional.Name, 13);
        ResolvedDesktopFont code = DesktopFontResolver.Resolve(requested, DesktopFontRole.Code);
        Assert.That(code.RequestedFamilyAvailable, Is.True); Assert.That(code.Requested, Is.EqualTo(requested)); Assert.That(code.IsFixedPitch, Is.True); Assert.That(code.Diagnostic, Does.Contain("fixed pitch"));
        foreach (DesktopFontRole role in new[] { DesktopFontRole.Application, DesktopFontRole.Commit, DesktopFontRole.Monospace })
        { ResolvedDesktopFont font = DesktopFontResolver.Resolve(requested, role); Assert.That(font.Family.Name, Is.EqualTo(proportional.Name)); Assert.That(font.IsFixedPitch, Is.False); Assert.That(font.Diagnostic, Is.Null); }
    }

    [AvaloniaTest]
    public void CodeChoicesUseRealFaceMetricsForEachStyleAndRemainUsableAfterRepeatedResolution()
    {
        foreach ((bool bold, bool italic) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            IReadOnlyList<FontFamily> choices = DesktopFontResolver.Families(DesktopFontRole.Code, bold, italic); Assert.That(choices, Is.Not.Empty);
            foreach (FontFamily family in choices)
            { Assert.That(FontManager.Current.TryGetGlyphTypeface(new Typeface(family, italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), out var glyph), Is.True); Assert.That(glyph!.Metrics.IsFixedPitch, Is.True); }
            ResolvedDesktopFont font = DesktopFontResolver.Resolve(new(choices[0].Name, 10, bold, italic), DesktopFontRole.Code);
            double narrow = Measure("iiii", font), wide = Measure("WWWW", font); Assert.That(narrow, Is.EqualTo(wide).Within(.01));
        }
        Assert.That(DesktopFontResolver.Families(DesktopFontRole.Application).Count, Is.GreaterThanOrEqualTo(DesktopFontResolver.Families(DesktopFontRole.Code).Count));
    }

    [AvaloniaTest]
    public void ApplyUsesPointConversionExactlyOnceAndDoesNotChangeOtherRoles()
    {
        FontFamily family = DesktopFontResolver.Families(DesktopFontRole.Code)[0]; FontPreferences preferences = new() { Code = new(family.Name, 10, true, true), Commit = new(family.Name, 9) };
        DesktopFontSession session = new(preferences, null); TextBox editor = new(); TextBlock commit = new();
        session.Apply(editor, DesktopFontRole.Code); session.Apply(commit, DesktopFontRole.Commit);
        Assert.That(editor.FontSize, Is.EqualTo(40d / 3).Within(1e-12)); Assert.That(editor.FontWeight, Is.EqualTo(FontWeight.Bold)); Assert.That(editor.FontStyle, Is.EqualTo(FontStyle.Italic));
        Assert.That(commit.FontSize, Is.EqualTo(12)); Assert.That(commit.FontWeight, Is.EqualTo(FontWeight.Normal));
        Assert.That(Measure("height", session.For(DesktopFontRole.Code)), Is.GreaterThan(0));
    }

    [AvaloniaTest]
    public void RendererRangeFailureFallsBackWithoutOverwritingTheRequestedFinitePoints()
    {
        FontSpec request = new("Consolas", 1e100); ResolvedDesktopFont font = DesktopFontResolver.Resolve(request, DesktopFontRole.Code);
        Assert.That(font.Requested, Is.EqualTo(request)); Assert.That(font.SizeInDips, Is.EqualTo(40d / 3).Within(1e-12)); Assert.That(font.Diagnostic, Does.Contain("cannot be rendered"));
    }

    private static double Measure(string text, ResolvedDesktopFont font) => new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(font.Family, font.Style, font.Weight), font.SizeInDips, Brushes.Black).Width;
    private sealed class Profile : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gitextensions-font-session-" + Guid.NewGuid().ToString("N"));
        internal FontPreferencesStore Store { get; }
        internal Profile() { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root); Store = new(); }
        public void Dispose() { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _old); if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
    }
}
