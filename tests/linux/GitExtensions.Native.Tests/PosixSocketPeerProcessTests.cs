using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using GitExtensions.Linux;
using NUnit.Framework;

namespace GitExtensions.Native.Tests;

[Category("PosixPeerProcess"), NonParallelizable, Platform("Linux,MacOsX")]
[SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
public sealed class PosixSocketPeerProcessTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Test]
    public async Task KernelReferenceWaitsForTheActualPeerAfterItsSocketCloses()
    {
        using LinuxUnixSocketDirectory directory = LinuxUnixSocketDirectory.Create("ge-peer-");
        using Socket listener = Listen(directory.SocketPath);
        ProcessStartInfo start = new(NativeProcessOwnershipTests.Executable) { UseShellExecute = false };
        foreach (string value in new[] { "--native-process-probe", "socket-peer", directory.DirectoryPath, directory.SocketPath })
        { start.ArgumentList.Add(value); }
        using Process child = Process.Start(start)!;
        try
        {
            using CancellationTokenSource timeout = new(Limit);
            using Socket socket = await listener.AcceptAsync(timeout.Token);
            using PosixSocketPeerProcess peer = await PosixSocketPeerProcess.ReceiveAsync(socket, timeout.Token);
            Assert.That(peer.ProcessId, Is.EqualTo(child.Id));
            await UntilFileAsync(Path.Combine(directory.DirectoryPath, "peer-ready.json"), timeout.Token);
            using JsonDocument ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory.DirectoryPath, "peer-ready.json")));
            Assert.That(ready.RootElement.GetProperty("owner").GetInt32(), Is.EqualTo(Environment.ProcessId));
            Assert.That(ready.RootElement.GetProperty("pid").GetInt32(), Is.EqualTo(child.Id));
            Task exited = peer.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(directory.DirectoryPath, "close-socket"), "close");
            await UntilFileAsync(Path.Combine(directory.DirectoryPath, "peer-closed.json"), timeout.Token);
            Assert.That(child.HasExited, Is.False);
            Assert.That(exited.IsCompleted, Is.False, "Closing the control channel is not proof of process exit.");
            await File.WriteAllTextAsync(Path.Combine(directory.DirectoryPath, "release"), "release");
            await Task.WhenAll(child.WaitForExitAsync(), exited).WaitAsync(Limit);
            Assert.That(child.ExitCode, Is.Zero);
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory.DirectoryPath, "close-socket"), "close");
            await File.WriteAllTextAsync(Path.Combine(directory.DirectoryPath, "release"), "release");
            await child.WaitForExitAsync().WaitAsync(Limit);
        }
    }

    [Test]
    public async Task CancellationBeforeAnOfferDoesNotConsumeARecordOrAdmitAProcess()
    {
        using LinuxUnixSocketDirectory directory = LinuxUnixSocketDirectory.Create("ge-peer-");
        using Socket listener = Listen(directory.SocketPath);
        using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(directory.SocketPath));
        using Socket socket = await listener.AcceptAsync();
        Assert.That(PosixSocketPeerProcess.Authenticate(socket), Is.EqualTo(Environment.ProcessId));
        using CancellationTokenSource cancellation = new();
        Task<PosixSocketPeerProcess> pending = PosixSocketPeerProcess.ReceiveAsync(socket, cancellation.Token);
        Assert.That(pending.IsCompleted, Is.False);
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await pending.WaitAsync(Limit));
        await client.SendAsync(new byte[] { (byte)'!' }, SocketFlags.None);
        Assert.ThrowsAsync<IOException>(async () => await PosixSocketPeerProcess.ReceiveAsync(socket).WaitAsync(Limit));
    }

    [TestCase((byte)'P')]
    [TestCase((byte)'!')]
    public async Task ARecordWithoutTheRequiredNativeReferenceIsRejected(byte marker)
    {
        using LinuxUnixSocketDirectory directory = LinuxUnixSocketDirectory.Create("ge-peer-");
        using Socket listener = Listen(directory.SocketPath);
        using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(directory.SocketPath));
        using Socket socket = await listener.AcceptAsync();
        await client.SendAsync(new[] { marker }, SocketFlags.None);
        Assert.ThrowsAsync<IOException>(async () => await PosixSocketPeerProcess.ReceiveAsync(socket).WaitAsync(Limit));
    }

    private static Socket Listen(string path)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path)); socket.Listen(1); return socket;
    }

    private static async Task UntilFileAsync(string path, CancellationToken token)
    { while (!File.Exists(path)) { await Task.Delay(10, token); } }
}
