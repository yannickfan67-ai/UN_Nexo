using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.ServerStatus.Tests;

internal static class StatusProtocolRegression
{
    public static async Task RunAsync()
    {
        await ExpectOfflineForStatusFrameAsync([0], "zero-length frame");

        await ExpectOfflineForRawResponseAsync(
            [0x80, 0x80, 0x80, 0x80, 0x10],
            "overflowing packet-length VarInt");

        await ExpectOfflineForStatusPayloadAsync(
            [0x00, 0x02, 0xC3, 0x28],
            "malformed UTF-8");

        var validJson = JsonSerializer.Serialize(new
        {
            version = new { name = "1.21.4", protocol = 769 },
            players = new { online = 1, max = 10 },
            description = new { text = "ok" }
        });
        using (var payload = new MemoryStream())
        {
            WriteVarInt(payload, 0);
            WriteString(payload, validJson);
            payload.WriteByte(0x00);
            await ExpectOfflineForStatusPayloadAsync(
                payload.ToArray(),
                "status trailing bytes");
        }

        await TestMismatchedPongKeepsStatusAsync();
        await TestOverallDeadlineBoundsPingAsync();
        TestInternationalizedTargetCanonicalization();

        Console.WriteLine("[server-status] packet framing and ping hardening");
    }

    private static async Task ExpectOfflineForStatusFrameAsync(
        byte[] framedResponse,
        string label)
    {
        await WithRawStatusServerAsync(
            async stream =>
            {
                await stream.WriteAsync(framedResponse);
                await stream.FlushAsync();
            },
            async target =>
            {
                var result = await new MinecraftServerStatusService(
                    TimeSpan.FromSeconds(1)).QueryAsync(target);
                Equal(ServerStatusState.Offline, result.State, label);
            });
    }

    private static async Task ExpectOfflineForRawResponseAsync(
        byte[] rawResponse,
        string label)
        => await ExpectOfflineForStatusFrameAsync(rawResponse, label);

    private static async Task ExpectOfflineForStatusPayloadAsync(
        byte[] payload,
        string label)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await ExpectOfflineForStatusFrameAsync(packet.ToArray(), label);
    }

    private static async Task TestMismatchedPongKeepsStatusAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadPacketAsync(stream);
            _ = await ReadPacketAsync(stream);
            await WriteValidStatusAsync(stream);

            var ping = await ReadPacketAsync(stream);
            var offset = 1;
            var sent = BinaryPrimitives.ReadInt64BigEndian(
                ping.AsSpan(offset, sizeof(long)));

            using var pong = new MemoryStream();
            WriteVarInt(pong, 1);
            Span<byte> wrong = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(wrong, sent + 1);
            pong.Write(wrong);
            pong.WriteByte(0x7F); // trailing bytes must also be rejected.
            await WritePacketAsync(stream, pong.ToArray());
        });

        var result = await new MinecraftServerStatusService(
            TimeSpan.FromSeconds(2)).QueryAsync(
                new MinecraftServerTarget("127.0.0.1", port));
        await server;

        Equal(ServerStatusState.Online, result.State,
            "mismatched pong must not discard valid status");
        Equal<long?>(null, result.LatencyMs,
            "mismatched/trailing pong must not produce latency");
    }

    private static async Task TestOverallDeadlineBoundsPingAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();

        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = client.GetStream();
                _ = await ReadPacketAsync(stream, stop.Token);
                _ = await ReadPacketAsync(stream, stop.Token);
                await Task.Delay(160, stop.Token);
                await WriteValidStatusAsync(stream, stop.Token);
                _ = await ReadPacketAsync(stream, stop.Token);
                await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });

        var service = new MinecraftServerStatusService(
            timeout: TimeSpan.FromMilliseconds(260),
            pingTimeout: TimeSpan.FromSeconds(2));
        var stopwatch = Stopwatch.StartNew();
        var result = await service.QueryAsync(
            new MinecraftServerTarget("127.0.0.1", port));
        stopwatch.Stop();

        stop.Cancel();
        await server;

        Equal(ServerStatusState.Online, result.State,
            "overall timeout during optional ping must preserve status");
        Equal<long?>(null, result.LatencyMs,
            "overall timeout during ping must leave latency unavailable");
        if (stopwatch.Elapsed > TimeSpan.FromMilliseconds(800))
            throw new InvalidOperationException(
                $"overall timeout was exceeded by optional ping: {stopwatch.Elapsed}.");
    }

    private static void TestInternationalizedTargetCanonicalization()
    {
        var target = MinecraftServerTarget.Parse("例子.测试");
        if (target.Host.Any(ch => ch > 0x7F))
            throw new InvalidOperationException(
                "IDN server host must be canonicalized to ASCII before handshake.");
        if (Encoding.UTF8.GetByteCount(target.Host) > 255)
            throw new InvalidOperationException(
                "canonical server host exceeds handshake limit.");

        Throws<FormatException>(
            () => MinecraftServerTarget.Parse(new string('界', 100) + ".example"),
            "oversized internationalized label");
    }

    private static async Task WithRawStatusServerAsync(
        Func<NetworkStream, Task> respond,
        Func<MinecraftServerTarget, Task> assert)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            _ = await ReadPacketAsync(stream);
            _ = await ReadPacketAsync(stream);
            await respond(stream);
        });

        await assert(new MinecraftServerTarget("127.0.0.1", port));
        await server;
    }

    private static async Task WriteValidStatusAsync(
        NetworkStream stream,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new
        {
            version = new { name = "1.21.4", protocol = 769 },
            players = new { online = 2, max = 20 },
            description = new { text = "protocol-regression" }
        });

        using var payload = new MemoryStream();
        WriteVarInt(payload, 0);
        WriteString(payload, json);
        await WritePacketAsync(
            stream,
            payload.ToArray(),
            cancellationToken);
    }

    private static async Task<byte[]> ReadPacketAsync(
        NetworkStream stream,
        CancellationToken cancellationToken = default)
    {
        var length = await ReadVarIntAsync(stream, cancellationToken);
        if (length is < 0 or > 1024 * 1024)
            throw new InvalidDataException("Invalid fixture packet length.");
        var payload = new byte[length];
        var read = 0;
        while (read < payload.Length)
        {
            var count = await stream.ReadAsync(
                payload.AsMemory(read),
                cancellationToken);
            if (count == 0)
                throw new EndOfStreamException();
            read += count;
        }
        return payload;
    }

    private static async Task<int> ReadVarIntAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var result = 0;
        var one = new byte[1];
        for (var index = 0; index < 5; index++)
        {
            var count = await stream.ReadAsync(one, cancellationToken);
            if (count == 0)
                throw new EndOfStreamException();
            var current = one[0];
            result |= (current & 0x7F) << (index * 7);
            if ((current & 0x80) == 0)
                return result;
        }
        throw new InvalidDataException("fixture VarInt too long");
    }

    private static async Task WritePacketAsync(
        NetworkStream stream,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
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
            throw new InvalidOperationException(
                $"{message}: expected '{expected}', got '{actual}'.");
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{message}: expected {typeof(TException).Name}.");
    }
}
