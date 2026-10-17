using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Skia;

namespace GitExtensions.FontStreamRegression;

internal static class Program
{
    private delegate bool ExportStream(out Stream? stream);

    private static int Main(string[] args)
    {
        if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
        { Console.Error.WriteLine("Expected absolute input.json and fresh receipt.json paths."); return 2; }
        ProbeReceipt receipt = new();
        try
        {
            ProbeProtocol.Require(OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64,
                "This version-linked probe requires Linux x64.");
            FileInfo inputFile = new(args[0]);
            ProbeProtocol.Require(inputFile.Length is > 0 and <= 4 * 1024 * 1024, "Input JSON exceeds its finite budget.");
            byte[] bytes = File.ReadAllBytes(args[0]); receipt.InputSha256 = ProbeProtocol.Hash(bytes);
            ProbeInput input = JsonSerializer.Deserialize<ProbeInput>(bytes) ?? throw new IOException("Missing input.");
            Run(input, Path.GetDirectoryName(args[0])!, receipt);
            receipt.Status = "SUCCESS";
        }
        catch (Exception error)
        {
            receipt.Status = "FAILED"; receipt.Error = error.GetType().Name + ": " + error.Message;
            Console.Error.WriteLine(receipt.Error);
        }
        try { ProbeProtocol.WriteJson(args[1], receipt); }
        catch (Exception error) { Console.Error.WriteLine("Receipt was not published: " + error.Message); return 3; }
        return receipt.Status == "SUCCESS" ? 0 : 1;
    }

    private static void Run(ProbeInput input, string fixture, ProbeReceipt receipt)
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("Linux only."); }
        string binaryRoot = AppContext.BaseDirectory;
        ProbeProtocol.Require(input.Version == 1 && !string.IsNullOrWhiteSpace(input.Family), "Unqualified input.");
        ProbeProtocol.Require(Path.GetFullPath(input.FontPath) == Path.Combine(fixture, "source.font"), "Font must be the private fixture source.");
        ProbeProtocol.Require(input.Binaries.SequenceEqual(ProbeProtocol.Snapshot(binaryRoot)), "Binary closure differs from parent pins.");
        byte[] source = ProbeProtocol.ReadFont(input.FontPath);
        ProbeProtocol.Require(source.LongLength == input.FontBytes && ProbeProtocol.Hash(source) == input.FontSha256, "Actual source bytes differ from input.");
        receipt.Family = input.Family;
        AppBuilder.Configure<ProbeApplication>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        FontManager manager = FontManager.Current;
        FontFamily family = manager.SystemFonts.FirstOrDefault(value => value.Name.Equals(input.Family, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The fc-match family is not in actual SystemFonts.");
        ProbeProtocol.Require(manager.SystemFonts.TryGetGlyphTypeface(family.Name, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out IGlyphTypeface? collection), "Regular collection resolution failed.");
        ProbeProtocol.Require(manager.TryGetGlyphTypeface(new Typeface(family), out IGlyphTypeface? glyph), "Regular public resolution failed.");
        ProbeProtocol.Require(collection is not null && glyph is not null && collection.FamilyName.Equals(glyph.FamilyName, StringComparison.OrdinalIgnoreCase), "Collection and public lookup disagree.");
        IGlyphTypeface resolved = glyph ?? throw new InvalidOperationException("Public resolution returned no glyph.");
        receipt.ResolvedFamily = resolved.FamilyName;
        ProbeProtocol.Require(resolved.GetType().FullName == "Avalonia.Skia.GlyphTypefaceImpl", "Unexpected backend glyph type.");
        MethodInfo method = resolved.GetType().GetMethod("TryGetStream", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException("Expected version-linked TryGetStream.");
        ParameterInfo[] parameters = method.GetParameters();
        ProbeProtocol.Require(method.ReturnType == typeof(bool) && parameters.Length == 1 && parameters[0].IsOut
            && parameters[0].ParameterType == typeof(Stream).MakeByRefType(), "TryGetStream signature changed.");
        ExportStream export = method.CreateDelegate<ExportStream>(resolved);
        receipt.Modules = new[] { typeof(Program).Assembly, typeof(FontManager).Assembly, typeof(SkiaPlatform).Assembly, typeof(SkiaSharp.SKTypeface).Assembly }
            .Select(assembly => new ModulePin(assembly.GetName().Name!, assembly.GetName().Version!.ToString(),
                assembly.ManifestModule.ModuleVersionId.ToString(), assembly.Location, ProbeProtocol.HashFile(assembly.Location))).ToArray();
        ModulePin backend = receipt.Modules.Single(module => module.Name == "Avalonia.Skia");
        ProbeProtocol.Require(backend.Version == "11.3.22.0" && backend.Sha256 == input.BackendSha256, "Loaded backend does not match parent.");
        ProbeProtocol.Require(receipt.Modules.Single(module => module.Name == "SkiaSharp").Version == "2.88.0.0", "SkiaSharp version changed.");
        using (Process current = Process.GetCurrentProcess())
        {
            string[] native = current.Modules.Cast<ProcessModule>().Select(module => module.FileName)
                .Where(path => Path.GetFileName(path) == "libSkiaSharp.so").Distinct(StringComparer.Ordinal).ToArray();
            ProbeProtocol.Require(native.Length == 1, "Expected one actually loaded native Skia module.");
            receipt.NativePath = native[0]; receipt.NativeSha256 = ProbeProtocol.HashFile(native[0]);
        }
        ProbeProtocol.Require(!Path.GetRelativePath(binaryRoot, receipt.NativePath).StartsWith("..", StringComparison.Ordinal)
            && receipt.NativeSha256 == input.NativeSha256, "Native Skia escaped the pinned probe closure.");
        receipt.RuntimePath = Environment.ProcessPath ?? throw new IOException("Runtime path unavailable.");
        receipt.RuntimeSha256 = ProbeProtocol.HashFile(receipt.RuntimePath);
        byte[] buffer = new byte[64 * 1024]; int passes = 0;
        (int PassesBefore, int PassesAfter, int Gen0Before, int Gen0After, int GenMaxBefore, int GenMaxAfter) VerifyExport()
        {
            int passesBefore = Volatile.Read(ref passes);
            int gen0Before = GC.CollectionCount(0), genMaxBefore = GC.CollectionCount(GC.MaxGeneration);
            bool obtained = export(out Stream? stream);
            int gen0After = GC.CollectionCount(0), genMaxAfter = GC.CollectionCount(GC.MaxGeneration);
            int passesAfter = Volatile.Read(ref passes);
            receipt.GcOverlap |= gen0After > gen0Before || genMaxAfter > genMaxBefore;
            ProbeProtocol.Require(obtained && stream is not null, "TryGetStream returned no stream.");
            using (Stream actual = stream ?? throw new IOException("No exported stream."))
            {
                ProbeProtocol.Require(actual.CanRead && actual.CanSeek && actual.Length == source.LongLength, "Export length/readability differs from actual source.");
                int offset = 0;
                while (offset < source.Length)
                {
                    int count = actual.Read(buffer, 0, Math.Min(buffer.Length, source.Length - offset));
                    ProbeProtocol.Require(count > 0 && buffer.AsSpan(0, count).SequenceEqual(source.AsSpan(offset, count)), "Export bytes differ from actual source.");
                    offset += count;
                }
                ProbeProtocol.Require(actual.ReadByte() == -1, "Export contains extra bytes.");
            }
            return (passesBefore, passesAfter, gen0Before, gen0After, genMaxBefore, genMaxAfter);
        }
        VerifyExport(); // Real file-byte oracle, before pressure; never self-baseline the export.
        receipt.GcOverlap = false; // Qualification-time GC cannot satisfy the pressure-phase oracle.
        CancellationTokenSource stop = new(); ManualResetEventSlim ready = new();
        long allocated = 0; Exception? workerError = null; bool workerEndedEarly = false;
        Thread worker = new(() =>
        {
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread(); byte[][] pressure = new byte[64][];
                Stopwatch timer = Stopwatch.StartNew();
                for (int pass = 0; pass < 20000 && timer.Elapsed < TimeSpan.FromSeconds(10) && !stop.IsCancellationRequested; pass++)
                {
                    for (int slot = 0; slot < pressure.Length; slot++) { pressure[slot] = new byte[4096]; }
                    GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: false);
                    if ((pass + 1) % 8 == 0) { GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false); GC.WaitForPendingFinalizers(); }
                    Interlocked.Increment(ref passes); ready.Set(); GC.KeepAlive(pressure);
                }
                allocated = GC.GetAllocatedBytesForCurrentThread() - before; workerEndedEarly = !stop.IsCancellationRequested;
            }
            catch (Exception error) { workerError = error; ready.Set(); }
        }) { IsBackground = true, Name = "Font stream GC pressure" };
        worker.Start();
        try
        {
            ProbeProtocol.Require(ready.Wait(TimeSpan.FromSeconds(2)), "GC worker did not start within its bound.");
            int initialPasses = Volatile.Read(ref passes);
            receipt.CollectionsBefore = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
            Stopwatch phase = Stopwatch.StartNew();
            for (int index = 0; index < ProbeProtocol.Exports; index++)
            {
                ProbeProtocol.Require(phase.Elapsed < TimeSpan.FromSeconds(10), "Export phase deadline exceeded.");
                Console.WriteLine(JsonSerializer.Serialize(new { phase = "enter", index, gcPasses = Volatile.Read(ref passes), ticks = Stopwatch.GetTimestamp() }));
                Console.Out.Flush();
                var gc = VerifyExport();
                receipt.Exports++; receipt.VerifiedBytes += source.LongLength;
                Console.WriteLine(JsonSerializer.Serialize(new { phase = "return", index, gc.PassesBefore, gc.PassesAfter,
                    gc.Gen0Before, gc.Gen0After, gc.GenMaxBefore, gc.GenMaxAfter, ticks = Stopwatch.GetTimestamp() }));
                Console.Out.Flush();
            }
            receipt.CollectionsAfter = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
            ProbeProtocol.Require(phase.Elapsed < TimeSpan.FromSeconds(10) && Volatile.Read(ref passes) > initialPasses && receipt.GcOverlap
                && receipt.CollectionsAfter.Where((count, generation) => count > receipt.CollectionsBefore[generation]).Any(),
                "No measured GC progression overlapped a TryGetStream call interval.");
        }
        finally
        {
            stop.Cancel(); receipt.WorkerJoined = worker.Join(TimeSpan.FromSeconds(2)); receipt.GcPasses = Volatile.Read(ref passes);
            if (receipt.WorkerJoined) { receipt.WorkerAllocatedBytes = allocated; stop.Dispose(); ready.Dispose(); }
            receipt.WorkerEndedEarly = workerEndedEarly; receipt.WorkerError = workerError?.ToString();
            GC.KeepAlive(glyph); GC.KeepAlive(collection); GC.KeepAlive(manager);
        }
        ProbeProtocol.Require(receipt.WorkerJoined && workerError is null && !workerEndedEarly && receipt.WorkerAllocatedBytes > 0,
            "GC worker failed, ended early, or did not join before teardown.");
        receipt.BinaryInputsUnchanged = input.Binaries.SequenceEqual(ProbeProtocol.Snapshot(binaryRoot));
        ProbeProtocol.Require(receipt.BinaryInputsUnchanged && ProbeProtocol.HashFile(input.FontPath) == input.FontSha256, "Inputs changed during pressure.");
    }

    private sealed class ProbeApplication : Application
    {
        public ProbeApplication() { }
    }
}
