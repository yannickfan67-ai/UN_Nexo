using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.ServerStatus.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            await TestStatusPingAsync();
            await TestTimeoutAsync();
            TestProtocolCompatibility();
            await TestFavoriteMigrationAndInstanceLinkAsync();
            Console.WriteLine("PASS server status regressions");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL server status regressions: " + ex);
            return 1;
        }
    }

    private static async Task TestStatusPingAsync()
    {
        Console.WriteLine("[server-status] status + pong");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadPacketAsync(stream); // handshake
            _ = await ReadPacketAsync(stream); // status request

            var statusJson = JsonSerializer.Serialize(new
            {
                version = new { name = "1.21.4", protocol = 769 },
                players = new { online = 7, max = 32 },
                description = new
                {
                    text = "§aNexo ",
                    extra = new[] { new { text = "test server" } }
                }
            });
            using var response = new MemoryStream();
            WriteVarInt(response, 0);
            WriteString(response, statusJson);
            await WritePacketAsync(stream, response.ToArray());

            var ping = await ReadPacketAsync(stream);
            await WritePacketAsync(stream, ping);
            await stream.FlushAsync();
        });

        var service = new MinecraftServerStatusService(TimeSpan.FromSeconds(2));
        var result = await service.QueryAsync(new MinecraftServerTarget("127.0.0.1", port));
        await server;

        Equal(ServerStatusState.Online, result.State, "status state");
        Equal(7, result.OnlinePlayers, "online players");
        Equal(32, result.MaxPlayers, "max players");
        Equal(769, result.ProtocolVersion, "protocol");
        Equal("1.21.4", result.VersionName, "version name");
        Equal("Nexo test server", result.Motd, "flattened MOTD");
        Equal(true, result.LatencyMs is >= 0, "latency should be measured");
    }

    private static async Task TestTimeoutAsync()
    {
        Console.WriteLine("[server-status] bounded timeout");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCancellation = new CancellationTokenSource();

        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(serverCancellation.Token);
                await using var stream = client.GetStream();
                _ = await ReadPacketAsync(stream, serverCancellation.Token);
                _ = await ReadPacketAsync(stream, serverCancellation.Token);
                await Task.Delay(TimeSpan.FromSeconds(5), serverCancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });

        var service = new MinecraftServerStatusService(TimeSpan.FromMilliseconds(180));
        var started = DateTime.UtcNow;
        var result = await service.QueryAsync(new MinecraftServerTarget("127.0.0.1", port));
        var elapsed = DateTime.UtcNow - started;
        serverCancellation.Cancel();
        await server;

        Equal(ServerStatusState.TimedOut, result.State, "timeout state");
        Equal(true, elapsed < TimeSpan.FromSeconds(2), "timeout must be bounded");
    }

    private static void TestProtocolCompatibility()
    {
        Console.WriteLine("[server-status] protocol compatibility");
        var online = new ServerStatusResult(
            ServerStatusState.Online,
            "example.test:25565",
            15,
            1,
            20,
            "Paper 1.21.4",
            769,
            "test",
            null);

        var matching = new GameInstance("a", "Modern", "1.21.4", "vanilla", DateTimeOffset.UtcNow);
        var mismatch = new GameInstance("b", "Legacy", "1.8.9", "vanilla", DateTimeOffset.UtcNow);
        var unknown = new GameInstance("c", "Snapshot", "26w37a", "vanilla", DateTimeOffset.UtcNow);

        Equal(ProtocolCompatibilityState.Match, MinecraftProtocolCompatibility.Compare(matching, online).State, "protocol match");
        Equal(ProtocolCompatibilityState.Mismatch, MinecraftProtocolCompatibility.Compare(mismatch, online).State, "protocol mismatch");
        var unknownResult = MinecraftProtocolCompatibility.Compare(unknown, online);
        Equal(ProtocolCompatibilityState.Unknown, unknownResult.State, "unknown protocol must remain unknown");
        Contains(unknownResult.Message, "will not guess", "unknown compatibility wording");
    }

    private static async Task TestFavoriteMigrationAndInstanceLinkAsync()
    {
        Console.WriteLine("[server-status] favorite migration + default instance");
        var root = Path.Combine(Path.GetTempPath(), "nexo-server-store-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var created = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(
                Path.Combine(root, "servers.json"),
                $"[{{\"id\":\"old\",\"name\":\"Old favorite\",\"address\":\"localhost:25565\",\"createdAt\":\"{created:O}\"}}]");

            var store = new ServerStoreService(new NexoPathService(root));
            var migrated = await store.GetAllAsync();
            Equal(1, migrated.Count, "legacy favorite count");
            Equal<string?>(null, migrated[0].DefaultInstanceId, "legacy favorite default link");

            var linked = await store.SetDefaultInstanceAsync("old", "instance-123");
            Equal("instance-123", linked?.DefaultInstanceId, "saved default instance id");
            var reloaded = await store.GetAllAsync();
            Equal("instance-123", reloaded[0].DefaultInstanceId, "persisted default instance id");

            var edited = await store.AddAsync("Renamed", "localhost:25565");
            Equal("instance-123", edited.DefaultInstanceId, "editing favorite must retain link");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken = default)
    {
        var length = await ReadVarIntAsync(stream, cancellationToken);
        if (length is < 0 or > 1024 * 1024)
            throw new InvalidDataException("Invalid test packet length.");
        var payload = new byte[length];
        var read = 0;
        while (read < payload.Length)
        {
            var count = await stream.ReadAsync(payload.AsMemory(read), cancellationToken);
            if (count == 0)
                throw new EndOfStreamException();
            read += count;
        }
        return payload;
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var result = 0;
        var position = 0;
        var one = new byte[1];
        while (position < 35)
        {
            var count = await stream.ReadAsync(one, cancellationToken);
            if (count == 0)
                throw new EndOfStreamException();
            var current = one[0];
            result |= (current & 0x7F) << position;
            if ((current & 0x80) == 0)
                return result;
            position += 7;
        }
        throw new InvalidDataException("VarInt too long.");
    }

    private static async Task WritePacketAsync(NetworkStream stream, byte[] payload)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray());
        await stream.FlushAsync();
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var current = unchecked((uint)value);
        do
        {
            var temp = (byte)(current & 0x7F);
            current >>= 7;
            if (current != 0)
                temp |= 0x80;
            stream.WriteByte(temp);
        } while (current != 0);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
    }

    private static void Contains(string value, string expected, string message)
    {
        if (!value.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{message}: '{expected}' not found in '{value}'.");
    }
}
