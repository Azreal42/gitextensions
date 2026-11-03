using System.Diagnostics;
using System.Text.Json;

internal static class NativeProcessProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 2 && args[0] == "hold")
        {
            await Console.OpenStandardOutput().WriteAsync("probe-child-output\n"u8.ToArray());
            await Console.OpenStandardError().WriteAsync("probe-child-error\n"u8.ToArray());
            await PublishAsync(Path.Combine(args[1], "child-ready.json"), new { pid = Environment.ProcessId });
            await UntilFileAsync(Path.Combine(args[1], "release")); return 0;
        }
        if (args.Length == 3 && args[0] == "tree")
        {
            bool independentPipes = args[2] == "private";
            ProcessStartInfo child = new(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = independentPipes, RedirectStandardError = independentPipes
            };
            foreach (string value in new[] { "--native-process-probe", "hold", args[1] }) { child.ArgumentList.Add(value); }
            using Process process = Process.Start(child)!;
            await UntilFileAsync(Path.Combine(args[1], "child-ready.json"));
            await PublishAsync(Path.Combine(args[1], "tree-ready.json"), new { parent = Environment.ProcessId, child = process.Id });
            while (!File.Exists(Path.Combine(args[1], "allow-parent-exit")) && !File.Exists(Path.Combine(args[1], "release")))
            { await Task.Delay(10); }
            return 0;
        }
        if (args.Length >= 3 && args[0] == "duplex")
        {
            byte[] prefix = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i % 251)).ToArray();
            Stream output = Console.OpenStandardOutput(), error = Console.OpenStandardError();
            await output.WriteAsync(prefix); await error.WriteAsync(prefix);
            using MemoryStream input = new(); await Console.OpenStandardInput().CopyToAsync(input);
            await output.WriteAsync(input.ToArray());
            await PublishAsync(args[1], new { arguments = args[3..], cwd = Directory.GetCurrentDirectory(),
                value = Environment.GetEnvironmentVariable("GITEXTENSIONS_NATIVE_PROBE_VALUE") });
            return int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        }
        return 2;
    }

    private static async Task UntilFileAsync(string path)
    { while (!File.Exists(path)) { await Task.Delay(10); } }

    private static async Task PublishAsync<T>(string path, T value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path);
    }
}
