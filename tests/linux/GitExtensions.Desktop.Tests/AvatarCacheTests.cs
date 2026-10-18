using System.Buffers.Binary;
using Avalonia.Media;
using GitCommands;
using GitExtensions.Linux.Desktop.Avatars;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed class AvatarCacheTests
{
    [TestCase(13, 13, false)]
    [TestCase(13, 14, true)]
    [TestCase(0, 30, false)]
    [TestCase(-7, 31, true)]
    public async Task DiskTtlUsesTheCapturedWindowsDaysAndItsExactFreshBoundary(int configuredDays, int ageDays, bool expired)
    {
        using OfflineAvatarFixture f = new(); int providers = 0;
        f.Provider = (_, _, _) => { providers++; return Task.FromResult<AvatarPng?>(OfflineAvatars.Image()); };
        f.Disk.Read = (_, _) => Task.FromResult<AvatarDiskEntry?>(new(OfflineAvatars.Image(), f.Clock.GetLocalNow().DateTime.AddDays(-ageDays)));
        await using AvatarService service = f.Create(new() { Provider = AvatarProvider.Default, CacheDays = configuredDays });
        AvatarOwnerScope owner = service.CreateOwnerScope("window");
        Assert.That((await owner.ReadAsync(OfflineAvatars.Request()).WaitAsync(OfflineAvatars.Limit)).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
        Assert.That(service.Snapshot.CapturedDiskCacheDays, Is.EqualTo(configuredDays < 1 ? 30 : configuredDays));
        Assert.That(providers, Is.EqualTo(expired ? 1 : 0)); Assert.That(f.Disk.Expired, Is.EqualTo(expired ? 1 : 0));
        Assert.That(f.Disk.Stages, Is.EqualTo(expired ? 1 : 0)); Assert.That(f.Disk.Commits, Is.EqualTo(expired ? 1 : 0));
        Assert.That(f.Codec.Calls.Count, Is.EqualTo(expired ? 0 : 1));
        if (expired)
        {
            string[] events = f.Disk.Events.ToArray();
            Assert.That(events.Select(item => item.Split(':')[0]), Is.EqualTo(new[] { "read", "expired", "stage", "commit" }));
        }
    }

    [Test]
    public async Task ChangedCacheDaysAndAClosedFamilyDoNotRecaptureTtlOrExpireRuntimeMemory()
    {
        using OfflineAvatarFixture f = new(); int providers = 0;
        f.Provider = (_, _, _) => { providers++; return Task.FromResult<AvatarPng?>(OfflineAvatars.Image()); };
        f.Disk.Read = (_, _) => Task.FromResult<AvatarDiskEntry?>(new(OfflineAvatars.Image(), f.Clock.GetLocalNow().DateTime.AddDays(-10)));
        AvatarPreferences preferences = new() { Provider = AvatarProvider.Default, CacheDays = 13 };
        await using AvatarService service = f.Create(preferences); long epoch = service.Snapshot.Epoch;
        await service.ApplySavedPolicy(preferences with { CacheDays = 1, ShowCommitInfo = false }).Completion.WaitAsync(OfflineAvatars.Limit);
        AvatarOwnerScope family = service.CreateOwnerScope("first family");
        Assert.That((await family.ReadAsync(OfflineAvatars.Request())).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
        Assert.That(providers, Is.Zero, "A ten-day disk entry still uses the captured thirteen days, not the new one-day setting.");
        await family.BeginClose().Completion.WaitAsync(OfflineAvatars.Limit); f.Clock.Advance(TimeSpan.FromDays(1000));
        AvatarOwnerScope reopened = service.CreateOwnerScope("new family");
        Assert.That((await reopened.ReadAsync(OfflineAvatars.Request())).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
        Assert.That(f.Disk.Reads, Is.EqualTo(1)); Assert.That(providers, Is.Zero, "The runtime MRU has no time expiration.");
        await service.ApplySavedPolicy(preferences with { CacheDays = 180 }).Completion.WaitAsync(OfflineAvatars.Limit);
        f.Disk.Read = (_, _) => Task.FromResult<AvatarDiskEntry?>(new(OfflineAvatars.Image(), f.Clock.GetLocalNow().DateTime.AddDays(-14)));
        await reopened.ReadAsync(OfflineAvatars.Request("new@example.invalid")).WaitAsync(OfflineAvatars.Limit);
        Assert.That(providers, Is.EqualTo(1)); Assert.That(f.Disk.Expired, Is.EqualTo(1));
        Assert.That(service.Snapshot.Epoch, Is.EqualTo(epoch)); Assert.That(service.Snapshot.CapturedDiskCacheDays, Is.EqualTo(13)); Assert.That(f.Disk.Clears, Is.Zero);
    }

    [Test]
    public async Task NoneWithRemoteFallbackReusesEvenAncientDiskImagesWithoutExpiry()
    {
        using OfflineAvatarFixture f = new(); f.Provider = (_, _, _) => throw new AssertionException("Provider None must reuse its non-expiring disk entry.");
        f.Disk.Read = (_, _) => Task.FromResult<AvatarDiskEntry?>(new(OfflineAvatars.Image(), f.Clock.GetLocalNow().DateTime.AddDays(-10000)));
        await using AvatarService service = f.Create(new() { Provider = AvatarProvider.None, Fallback = AvatarFallbackType.Retro, CacheDays = 1 });
        Assert.That((await service.CreateOwnerScope("window").ReadAsync(OfflineAvatars.Request())).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
        Assert.That(f.Disk.Reads, Is.EqualTo(1)); Assert.That(f.Disk.Expired, Is.Zero); Assert.That(f.Disk.Stages, Is.Zero); Assert.That(f.Handler.Requests, Is.Empty);
    }

    [Test]
    public async Task LocalOnlyInitialsNeitherReadsNorStagesDisk()
    {
        using OfflineAvatarFixture f = new(); f.Disk.Read = (_, _) => throw new AssertionException("A local-only chain must not read persistent cache.");
        f.Disk.Stage = (_, _, _) => throw new AssertionException("A local-only chain must not stage persistent cache.");
        await using AvatarService service = f.Create(new() { Provider = AvatarProvider.None, Fallback = AvatarFallbackType.AuthorInitials });
        AvatarOwnerScope owner = service.CreateOwnerScope("window");
        Assert.That((await owner.ReadAsync(OfflineAvatars.Request())).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
        Assert.That((await owner.ReadAsync(OfflineAvatars.Request())).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
        Assert.That(f.InitialsCalls, Is.EqualTo(1)); Assert.That(f.Disk.Reads, Is.Zero); Assert.That(f.Disk.Stages, Is.Zero); Assert.That(f.Handler.Requests, Is.Empty);
    }

    [Test]
    public void DefaultMruRetainsTwoHundredEntriesRefreshesHitsAndReplacesWithoutGrowing()
    {
        AvatarMemoryCache memory = new(); AvatarPng original = OfflineAvatars.Image(), replacement = new(OfflineAvatars.Bytes, 42);
        for (int i = 0; i < 200; i++) { memory.Add(i.ToString(), original); }
        Assert.That(memory.TryGet("0", out AvatarPng? retained), Is.True); Assert.That(retained, Is.SameAs(original));
        memory.Add("200", original); Assert.That(memory.TryGet("1", out _), Is.False); Assert.That(memory.TryGet("0", out _), Is.True);
        memory.Add("2", replacement); memory.Add("201", original);
        Assert.That(memory.Count, Is.EqualTo(200)); Assert.That(memory.TryGet("3", out _), Is.False);
        Assert.That(memory.TryGet("2", out AvatarPng? changed), Is.True); Assert.That(changed, Is.SameAs(replacement));
        memory.Clear(); Assert.That(memory.TryGet("2", out _), Is.False); Assert.That(memory.Count, Is.Zero);
    }

    [TestCase("raw-email")]
    [TestCase("name")]
    [TestCase("size")]
    [TestCase("font")]
    [TestCase("culture")]
    [TestCase("weight")]
    [TestCase("style")]
    public async Task CacheReuseKeepsAllRenderingIdentityInputsDistinctAcrossOwners(string changedField)
    {
        using OfflineAvatarFixture f = new(); int providers = 0;
        f.Provider = (_, _, _) => { providers++; return Task.FromResult<AvatarPng?>(OfflineAvatars.Image()); };
        await using AvatarService service = f.Create(); AvatarOwnerScope first = service.CreateOwnerScope("first"), second = service.CreateOwnerScope("second");
        AvatarRequest original = OfflineAvatars.Request(); AvatarRequest changed = changedField switch
        {
            "raw-email" => original with { Email = " TEST@Example.com " }, "name" => original with { Name = "Different Person" },
            "size" => original with { PixelSize = 128 }, "font" => original with { RenderIdentity = original.RenderIdentity with { FontFamily = "another-offline-font" } },
            "culture" => original with { RenderIdentity = original.RenderIdentity with { CultureName = "tr-TR" } },
            "weight" => original with { RenderIdentity = original.RenderIdentity with { Weight = FontWeight.Bold } },
            "style" => original with { RenderIdentity = original.RenderIdentity with { Style = FontStyle.Italic } }, _ => throw new ArgumentOutOfRangeException(nameof(changedField))
        };
        Assert.That((await first.ReadAsync(original)).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
        Assert.That((await second.ReadAsync(changed)).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
        Assert.That((await second.ReadAsync(original)).Outcome, Is.EqualTo(AvatarReadOutcome.Ready));
        Assert.That(providers, Is.EqualTo(2)); Assert.That(f.Memory.Count, Is.EqualTo(2));
    }

    [TestCase(-9, 64)]
    [TestCase(0, 64)]
    [TestCase(999, 512)]
    public async Task NormalizedSizeReusesItsCanonicalImageRatherThanProducingAnotherWorker(int requested, int canonical)
    {
        using OfflineAvatarFixture f = new(); List<int> sizes = [];
        f.Provider = (_, request, _) => { sizes.Add(request.PixelSize); return Task.FromResult<AvatarPng?>(OfflineAvatars.Image()); };
        await using AvatarService service = f.Create(); AvatarOwnerScope owner = service.CreateOwnerScope("window");
        await owner.ReadAsync(OfflineAvatars.Request(pixels: requested)); await owner.ReadAsync(OfflineAvatars.Request(pixels: canonical));
        Assert.That(sizes, Is.EqualTo(new[] { canonical }));
    }

    [Test]
    public void PngOwnsItsInputAndReturnedBytesAndExposesOnlyAReadOnlyStream()
    {
        byte[] input = OfflineAvatars.Bytes; AvatarPng png = new(input); input[0] = 0; byte[] copy = png.CopyBytes(); copy[1] = 0;
        Assert.That(png.CopyBytes(), Is.EqualTo(OfflineAvatars.Bytes));
        using Stream stream = png.OpenRead(); Assert.That(stream.CanWrite, Is.False); Assert.That(stream.ReadByte(), Is.EqualTo(137));
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(0));
        Assert.That(((MemoryStream)stream).TryGetBuffer(out _), Is.False);
        AvatarPng epoch = png.WithEpoch(7); Assert.That(epoch.Epoch, Is.EqualTo(7)); Assert.That(png.Epoch, Is.Zero);
        Assert.That(epoch.CopyBytes(), Is.EqualTo(OfflineAvatars.Bytes));
    }

    [TestCase(0, 1)]
    [TestCase(1, 0)]
    [TestCase(2049, 1)]
    [TestCase(1, 2049)]
    public void PngRejectsUnsafeDimensionsBeforeAnyDecoderOrUiIsUsed(int width, int height)
    {
        byte[] bytes = OfflineAvatars.Bytes; BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), (uint)height);
        Assert.Throws<ArgumentException>(() => new AvatarPng(bytes));
    }

    [Test]
    public async Task RealDiskWritesAreInvisibleUntilCommitAndDisposingAPreparedWriteLeavesNoStagingFiles()
    {
        using AvatarTestDirectory directory = new(); AvatarDiskCache disk = new(directory.Path); string key = new('a', 64);
        Assert.That(Directory.Exists(directory.Path), Is.False, "Constructing a cache must not create its directory.");
        using (IAvatarCacheWrite abandoned = await disk.StageAsync(key, OfflineAvatars.Image(), default))
        {
            Assert.That(await disk.ReadAsync(key, default), Is.Null); Assert.That(Directory.GetFiles(directory.Path).Single(), Does.EndWith(".tmp"));
        }
        Assert.That(Directory.GetFiles(directory.Path), Is.Empty);
        using (IAvatarCacheWrite staged = await disk.StageAsync(key, OfflineAvatars.Image(), default))
        {
            Assert.That(await disk.ReadAsync(key, default), Is.Null); staged.Commit();
            Assert.That((await disk.ReadAsync(key, default))!.Image.CopyBytes(), Is.EqualTo(OfflineAvatars.Bytes));
        }
        Assert.That(Directory.GetFiles(directory.Path).Select(System.IO.Path.GetFileName), Is.EqualTo(new[] { key + ".png" }));
    }

    [Test]
    public async Task InvalidOversizedAndSymlinkDiskEntriesAreNotReusable()
    {
        using AvatarTestDirectory directory = new(); Directory.CreateDirectory(directory.Path); AvatarDiskCache disk = new(directory.Path);
        string invalid = new('a', 64), oversized = new('b', 64), symlink = new('c', 64);
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, invalid + ".png"), "not an image");
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, oversized + ".png"), new byte[AvatarImageTransport.MaximumResponseBytes + 1]);
        string source = System.IO.Path.Combine(directory.Path, "source.png"); await File.WriteAllBytesAsync(source, OfflineAvatars.Bytes);
        File.CreateSymbolicLink(System.IO.Path.Combine(directory.Path, symlink + ".png"), source);
        Assert.That(await disk.ReadAsync(invalid, default), Is.Null); Assert.That(await disk.ReadAsync(oversized, default), Is.Null);
        Assert.That(await disk.ReadAsync(symlink, default), Is.Null); Assert.That(await File.ReadAllBytesAsync(source), Is.EqualTo(OfflineAvatars.Bytes));
    }

    [Test]
    public async Task RealDiskClearAndExpiredDeletionAreConfinedToTheExplicitCacheDirectory()
    {
        using AvatarTestDirectory directory = new(); Directory.CreateDirectory(directory.Path); string own = System.IO.Path.Combine(directory.Path, "cache");
        string outside = System.IO.Path.Combine(directory.Path, "keep.png"); await File.WriteAllBytesAsync(outside, OfflineAvatars.Bytes);
        AvatarDiskCache disk = new(own); string first = new('a', 64), second = new('b', 64);
        using (IAvatarCacheWrite write = await disk.StageAsync(first, OfflineAvatars.Image(), default)) { write.Commit(); }
        using (IAvatarCacheWrite write = await disk.StageAsync(second, OfflineAvatars.Image(), default)) { write.Commit(); }
        disk.DeleteExpired(first); Assert.That(await disk.ReadAsync(first, default), Is.Null); Assert.That(await disk.ReadAsync(second, default), Is.Not.Null);
        await disk.ClearAsync(default); Assert.That(Directory.GetFiles(own), Is.Empty); Assert.That(await File.ReadAllBytesAsync(outside), Is.EqualTo(OfflineAvatars.Bytes));
    }

    [Test]
    public async Task InvalidKeysAndCanceledStagingCannotEscapeOrLeaveTemporaryFiles()
    {
        using AvatarTestDirectory directory = new(); AvatarDiskCache disk = new(directory.Path);
        Assert.CatchAsync<ArgumentException>(async () => await disk.StageAsync("../escape", OfflineAvatars.Image(), default));
        Assert.That(await disk.ReadAsync("../escape", default), Is.Null);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await disk.StageAsync(new string('a', 64), OfflineAvatars.Image(), canceled.Token));
        Assert.That(Directory.Exists(directory.Path), Is.False);
    }

    private sealed class AvatarTestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitextensions-avatar-unit-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); } }
    }
}
