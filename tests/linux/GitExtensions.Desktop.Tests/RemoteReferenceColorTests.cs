using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Desktop.Controls;
using GitExtensions.Linux;
using NUnit.Framework;
using Fixture = GitExtensions.Desktop.Tests.ReferenceDialogTests.ReferenceFixture;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public sealed class RemoteReferenceColorTests
{
    [AvaloniaTest]
    public async Task EffectiveRemoteColorsRespectSubsectionCaseLongestSlashPrefixAndDisabledSections()
    {
        using Fixture f = await Fixture.CreateAsync();
        foreach (string name in new[] { "Team", "team", "Team/Upstream.Blue", "Team/Uncolored", "Teamwork" }) { await AddRemoteReferenceAsync(f, name); }
        await f.Repository.SetConfigAsync("REMOTE.Team.COLOR", "#123ABC");
        await f.Repository.SetConfigAsync("remote.team.color", "#456DEF");
        await f.Repository.SetConfigAsync("remote.Team/Upstream.Blue.color", "SteelBlue");
        await File.AppendAllTextAsync(f.PathInRepository(".git/config"), "\n[-remote \"Disabled\"]\n\turl = disabled-not-contacted\n\tcolor = \"#FF00FF\"\n");
        await f.GitAsync("update-ref", "refs/remotes/Disabled/main", "HEAD");
        await f.GitAsync("update-ref", "refs/remotes/TEAM/main", "HEAD");
        await f.GitAsync("update-ref", "refs/remotes/TeamExtra/main", "HEAD");
        await f.GitAsync("symbolic-ref", "refs/remotes/Team/HEAD", "refs/remotes/Team/main");
        await f.GitAsync("branch", "Team/main", "HEAD"); await f.GitAsync("tag", "Team/v1", "HEAD");
        IReadOnlyList<GitReference> references = await f.Repository.GetReferencesAsync();
        string[] nativeReferences = (await f.GitAsync("for-each-ref", "--format=%(refname)", "refs/remotes/")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(references.Where(reference => reference.IsRemote).Select(reference => reference.Name), Is.EquivalentTo(nativeReferences));
        Assert.That(Reference(references, "refs/remotes/Team/main").RemoteColor, Is.EqualTo("#123ABC"));
        Assert.That(Reference(references, "refs/remotes/Team/HEAD").RemoteColor, Is.EqualTo("#123ABC"));
        Assert.That(Reference(references, "refs/remotes/team/main").RemoteColor, Is.EqualTo("#456DEF"));
        Assert.That(Reference(references, "refs/remotes/Team/Upstream.Blue/main").RemoteColor, Is.EqualTo("SteelBlue"));
        Assert.That(Reference(references, "refs/remotes/Team/Uncolored/main").RemoteColor, Is.Null,
            "The longest configured remote owns its references even when it has no custom color.");
        foreach (string name in new[] { "refs/remotes/Teamwork/main", "refs/remotes/TeamExtra/main", "refs/remotes/TEAM/main", "refs/remotes/Disabled/main", "refs/heads/Team/main", "refs/tags/Team/v1" })
        { Assert.That(Reference(references, name).RemoteColor, Is.Null, name); }
        Assert.That((await f.Repository.GetConfigAsync("^-remote\\..*\\.color$")).Select(setting => setting.Value), Is.EqualTo(new[] { "#FF00FF" }),
            "The disabled color really exists but must not color active references.");
    }

    [AvaloniaTest]
    public async Task RemoteColorUsesGitEffectiveIncludesLastValueAndCommandConfigurationAndResetRevealsInheritedColor()
    {
        using Fixture f = await Fixture.CreateAsync(); await AddRemoteReferenceAsync(f, "origin");
        string global = f.Output("global.gitconfig"), include = f.Output("included.gitconfig");
        await f.GitAsync("config", "--file", global, "remote.origin.color", "#102030");
        await f.GitAsync("config", "--file", include, "remote.origin.color", "#203040");
        f.SetEnvironment("GIT_CONFIG_GLOBAL", global);
        Assert.That(await ReadColorAsync(f, "origin"), Is.EqualTo("#102030"));
        await f.Repository.SetConfigAsync("include.path", include);
        Assert.That(await ReadColorAsync(f, "origin"), Is.EqualTo("#203040"));
        // Include precedence follows physical file order. Append a new remote
        // section after include rather than editing the earlier URL section.
        await File.AppendAllTextAsync(f.PathInRepository(".git/config"), "\n[remote \"origin\"]\n\tcolor = \"#304050\"\n\tcolor = \"#405060\"\n");
        Assert.That((await f.GitAsync("config", "--get-all", "remote.origin.color")).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            Is.EqualTo(new[] { "#102030", "#203040", "#304050", "#405060" }));
        Assert.That((await f.GitAsync("config", "--get", "remote.origin.color")).StandardOutput.TrimEnd('\n'), Is.EqualTo("#405060"));
        Assert.That(await ReadColorAsync(f, "origin"), Is.EqualTo("#405060"), "The final effective value wins; duplicate config entries must not throw.");
        f.SetEnvironment("GIT_CONFIG_COUNT", "1"); f.SetEnvironment("GIT_CONFIG_KEY_0", "remote.origin.color"); f.SetEnvironment("GIT_CONFIG_VALUE_0", "#506070");
        Assert.That(await ReadColorAsync(f, "origin"), Is.EqualTo("#506070"));
        f.SetEnvironment("GIT_CONFIG_COUNT", null); f.SetEnvironment("GIT_CONFIG_KEY_0", null); f.SetEnvironment("GIT_CONFIG_VALUE_0", null);
        await f.Repository.UnsetConfigAsync("remote.origin.color");
        Assert.That(await ReadColorAsync(f, "origin"), Is.EqualTo("#203040"), "Resetting local color must reveal the effective included value.");
        await File.AppendAllTextAsync(f.PathInRepository(".git/config"), "\n[remote \"origin\"]\n\tcolor = \"\"\n");
        Assert.That((await f.GitAsync("config", "--get", "remote.origin.color")).StandardOutput, Is.EqualTo("\n"));
        Assert.That(await ReadColorAsync(f, "origin"), Is.Null, "An explicit empty override chooses the ordinary remote palette.");
        await f.Repository.UnsetConfigAsync("remote.origin.color"); await f.Repository.UnsetConfigAsync("include.path");
        f.SetEnvironment("GIT_CONFIG_GLOBAL", "/dev/null");
        Assert.That(await ReadColorAsync(f, "origin"), Is.Null);
    }

    [AvaloniaTest]
    public async Task RealGitColorChangesInvalidValuesAndResetRedrawTheRemoteBadgeInBothThemes()
    {
        using Fixture f = await Fixture.CreateAsync(); await AddRemoteReferenceAsync(f, "origin");
        CommitGraph graph = new() { Commits = await f.Repository.GetLogAsync(1), Width = 220 };
        Window window = new() { Width = 240, Height = 80, Content = graph };
        try
        {
            window.Show();
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                foreach (string configured in new[] { "#123ABC", "SteelBlue", "invalid-remote-color" })
                {
                    await f.Repository.SetConfigAsync("remote.origin.color", configured);
                    GitReference reference = Reference(await f.Repository.GetReferencesAsync(), "refs/remotes/origin/main");
                    Assert.That(reference.RemoteColor, Is.EqualTo(configured)); graph.References = [reference];
                    Color expected = Color.TryParse(configured, out Color valid) ? valid : DefaultRemoteColor(theme == ThemeVariant.Dark);
                    Assert.That(CommitGraph.ReferenceColor(reference, theme == ThemeVariant.Dark), Is.EqualTo(expected));
                    using WriteableBitmap frame = Frame(window);
                    Assert.That(PixelsOfColor(frame, expected), Is.GreaterThan(0), "The actual reference badge must use the current effective remote color.");
                    Save(frame, "remote-color-" + theme + "-" + (configured.StartsWith('#') ? "hex" : configured) + ".png");
                }
                await f.Repository.UnsetConfigAsync("remote.origin.color");
                GitReference reset = Reference(await f.Repository.GetReferencesAsync(), "refs/remotes/origin/main");
                Assert.That(reset.RemoteColor, Is.Null); graph.References = [reset];
                using WriteableBitmap restored = Frame(window);
                Assert.That(PixelsOfColor(restored, DefaultRemoteColor(theme == ThemeVariant.Dark)), Is.GreaterThan(0));
                Assert.That(PixelsOfColor(restored, Color.Parse("#123ABC")), Is.Zero, "Reset must remove the old custom label color after reloading references.");
                Save(restored, "remote-color-" + theme + "-reset.png");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void MissingOrInvalidRemoteColorsAndEveryNonRemoteReferenceKeepTheirOriginalPalette()
    {
        foreach (bool dark in new[] { false, true })
        {
            foreach (string? configured in new string?[] { null, "", "#not-a-color", "bad-color" })
            {
                GitReference reference = new("refs/remotes/origin/main", "hash", null, false) { RemoteColor = configured };
                Assert.That(CommitGraph.ReferenceColor(reference, dark), Is.EqualTo(DefaultRemoteColor(dark)));
            }
            foreach ((string name, string light, string darkColor) in new[]
            {
                ("refs/heads/main", "#008000", "#7FE28A"), ("refs/tags/v1", "#00008b", "#40BAF7"), ("refs/notes/commits", "#808080", "#cfb3b3")
            })
            {
                GitReference reference = new(name, "hash", null, true) { RemoteColor = "#123ABC" };
                Assert.That(CommitGraph.ReferenceColor(reference, dark), Is.EqualTo(Color.Parse(dark ? darkColor : light)), name);
            }
        }
    }

    private static GitReference Reference(IReadOnlyList<GitReference> references, string name) => references.Single(reference => reference.Name == name);
    private static async Task<string?> ReadColorAsync(Fixture f, string remote) => Reference(await f.Repository.GetReferencesAsync(), "refs/remotes/" + remote + "/main").RemoteColor;
    private static async Task AddRemoteReferenceAsync(Fixture f, string name)
    {
        // Git can read existing nested remote subsections even when newer
        // versions refuse to create that overlap through `remote add`.
        await f.GitAsync("config", "--local", "--add", "remote." + name + ".url", f.Repository.Path);
        await f.GitAsync("update-ref", "refs/remotes/" + name + "/main", "HEAD");
    }
    private static Color DefaultRemoteColor(bool dark) => Color.Parse(dark ? "#fd9797" : "#8b0009");
    private static WriteableBitmap Frame(Window window)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
        return window.CaptureRenderedFrame() ?? throw new AssertionException("No rendered reference badge.");
    }
    private static void Save(WriteableBitmap frame, string name)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, name); frame.Save(path); TestContext.AddTestAttachment(path);
    }
    private static int PixelsOfColor(WriteableBitmap frame, Color color)
    {
        using ILockedFramebuffer pixels = frame.Lock(); byte[] bytes = new byte[pixels.RowBytes * pixels.Size.Height];
        Marshal.Copy(pixels.Address, bytes, 0, bytes.Length); bool bgra = pixels.Format == PixelFormat.Bgra8888; int count = 0;
        for (int y = 0; y < pixels.Size.Height; y++) { for (int x = 0; x < pixels.Size.Width; x++)
        {
            int offset = y * pixels.RowBytes + x * 4;
            if (bytes[offset + (bgra ? 2 : 0)] == color.R && bytes[offset + 1] == color.G && bytes[offset + (bgra ? 0 : 2)] == color.B && bytes[offset + 3] == color.A) { count++; }
        } }
        return count;
    }
}
