using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;

namespace GitExtensions.Desktop.Tests;

public sealed class UpdateCheckStateStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00Z", CultureInfo.InvariantCulture);
    [Test]
    public void ClaimIsDurableBeforeARequestAndOnlyMoreThanAWeekAllowsAnotherAttempt()
    {
        using Scope f = new(); Assert.That(f.Store.TryClaim(Now), Is.True);
        byte[] before = File.ReadAllBytes(f.Store.FilePath);
        Assert.That(f.Store.TryClaim(Now), Is.False); Assert.That(new UpdateCheckStateStore(f.Root).TryClaim(Now.AddDays(7)), Is.False);
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(before));
        Assert.That(new UpdateCheckStateStore(f.Root).TryClaim(Now.AddDays(7).AddTicks(1)), Is.True);
        Assert.That(f.Read()["LastUpdateCheckUtc"]!.GetValue<string>(), Is.EqualTo(Now.AddDays(7).AddTicks(1).ToString("O", CultureInfo.InvariantCulture)));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(f.Store.FilePath)!, "*.tmp"), Is.Empty);
    }
    [Test]
    public void FutureChecksStayThrottledAfterTheClockMovesBackwardsAndOffsetsAreComparedAsInstants()
    {
        using Scope f = new(); f.Write(new JsonObject { ["LastUpdateCheckUtc"] = Now.ToOffset(TimeSpan.FromHours(2)).ToString("O", CultureInfo.InvariantCulture) }.ToJsonString());
        byte[] before = File.ReadAllBytes(f.Store.FilePath);
        Assert.That(f.Store.TryClaim(Now.AddDays(-1)), Is.False); Assert.That(f.Store.TryClaim(Now.AddDays(7)), Is.False);
        Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(before)); Assert.That(f.Store.TryClaim(Now.AddDays(8)), Is.True);
    }
    [Test]
    public void AnExpiredCheckPreservesUnknownStateAndNeverWritesEditablePreferences()
    {
        using Scope f = new(); f.Write(new JsonObject { ["LastUpdateCheckUtc"] = Now.AddDays(-8).ToString("O", CultureInfo.InvariantCulture), ["future"] = JsonNode.Parse("[17,null,\"Ω\"]") }.ToJsonString());
        Assert.That(f.Store.TryClaim(Now), Is.True); Assert.That(JsonNode.DeepEquals(f.Read()["future"], JsonNode.Parse("[17,null,\"Ω\"]")), Is.True);
        Assert.That(Directory.GetFiles(f.Root, "preferences.json", SearchOption.AllDirectories), Is.Empty);
    }
    [TestCase("")] [TestCase("broken")] [TestCase("[]")] [TestCase("{\"LastUpdateCheckUtc\":false}")] [TestCase("{\"LastUpdateCheckUtc\":\"tomorrow\"}")]
    public void AnInvalidStateCannotBeSilentlyRepairedOrConsumed(string invalid)
    {
        using Scope f = new(); f.Write(invalid); byte[] bytes = File.ReadAllBytes(f.Store.FilePath);
        Assert.Catch<JsonException>(() => f.Store.TryClaim(Now)); Assert.That(File.ReadAllBytes(f.Store.FilePath), Is.EqualTo(bytes));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(f.Store.FilePath)!, "*.tmp"), Is.Empty);
    }
    [Test]
    public void CancellationBeforeTheClaimCreatesNoDirectoryOrState()
    {
        using Scope f = new(); using CancellationTokenSource stop = new(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => f.Store.TryClaim(Now, stop.Token)); Assert.That(Directory.Exists(f.Root), Is.False);
    }
    [Test]
    public void AnAlreadyOwnedClaimCannotBeClaimedByAnotherWindow()
    {
        using Scope f = new(); Directory.CreateDirectory(Path.GetDirectoryName(f.Store.FilePath)!);
        using (FileStream lease = new(f.Store.FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        { Assert.That(f.Store.TryClaim(Now), Is.False); Assert.That(File.Exists(f.Store.FilePath), Is.False); }
        Assert.That(f.Store.TryClaim(Now), Is.True);
    }
    [Test]
    public void CompetingClaimsOnlyAllowOneAttempt()
    {
        using Scope f = new(); bool[] accepted = new bool[12];
        Parallel.For(0, accepted.Length, index => accepted[index] = new UpdateCheckStateStore(f.Root).TryClaim(Now));
        Assert.That(accepted.Count(value => value), Is.EqualTo(1)); Assert.That(File.Exists(f.Store.FilePath), Is.True);
    }
    private sealed class Scope : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "update-check-state-" + Guid.NewGuid().ToString("N"));
        internal UpdateCheckStateStore Store { get; }
        internal Scope() => Store = new(Root);
        internal void Write(string text) { Directory.CreateDirectory(Path.GetDirectoryName(Store.FilePath)!); File.WriteAllText(Store.FilePath, text); }
        internal JsonObject Read() => JsonNode.Parse(File.ReadAllBytes(Store.FilePath))!.AsObject();
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}
