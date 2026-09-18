using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftServerStatusService
{
    private const int MaxPacketBytes = 1024 * 1024;
    private const int MaxStringBytes = 1024 * 1024;
    private static readonly Regex LegacyFormattingRegex = new("§.", RegexOptions.Compiled);

    private readonly TimeSpan _timeout;

    public MinecraftServerStatusService(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(4);
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<ServerStatusResult> QueryAsync(MinecraftServerTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;
        try
        {
            using var client = new TcpClient(); client.NoDelay = true;
            await client.ConnectAsync(target.Host, target.Port, token);
            await using var stream = client.GetStream();
            await WriteHandshakeAsync(stream, target, token);
            await stream.WriteAsync(new byte[] { 1, 0 }, token); await stream.FlushAsync(token);
            var responsePayload = await ReadPacketAsync(stream, token);
            var offset = 0; var packetId = ReadVarInt(responsePayload, ref offset);
            if (packetId != 0) throw new InvalidDataException($"Unexpected status packet id {packetId}.");
            var json = ReadString(responsePayload, ref offset);
            var parsed = ParseStatus(json, target.Authority);
            long? latency = null;
            try
            {
                var pingPayload = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var packet = new byte[1 + sizeof(long)]; packet[0] = 1;
                BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(1), pingPayload);
                await WritePacketAsync(stream, packet, token); await stream.FlushAsync(token);
                var stopwatch = Stopwatch.StartNew(); var pongPayload = await ReadPacketAsync(stream, token); stopwatch.Stop();
                var pongOffset = 0;
                if (ReadVarInt(pongPayload, ref pongOffset) == 1 && pongOffset + sizeof(long) <= pongPayload.Length)
                { _ = BinaryPrimitives.ReadInt64BigEndian(pongPayload.AsSpan(pongOffset, sizeof(long))); latency = stopwatch.ElapsedMilliseconds; }
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException) { }
            return parsed with { LatencyMs = latency };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { return new ServerStatusResult(ServerStatusState.TimedOut, target.Authority, null, null, null, null, null, string.Empty, $"No status response within {_timeout.TotalSeconds:0.#} seconds."); }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or JsonException)
        { return new ServerStatusResult(ServerStatusState.Offline, target.Authority, null, null, null, null, null, string.Empty, ex.Message); }
    }

    private static async Task WriteHandshakeAsync(NetworkStream stream, MinecraftServerTarget target, CancellationToken cancellationToken)
    { using var payload = new MemoryStream(); WriteVarInt(payload, 0); WriteVarInt(payload, -1); WriteString(payload, target.Host); Span<byte> port = stackalloc byte[sizeof(ushort)]; BinaryPrimitives.WriteUInt16BigEndian(port, checked((ushort)target.Port)); payload.Write(port); WriteVarInt(payload, 1); await WritePacketAsync(stream, payload.ToArray(), cancellationToken); }
    private static async Task WritePacketAsync(NetworkStream stream, byte[] payload, CancellationToken cancellationToken)
    { using var packet = new MemoryStream(); WriteVarInt(packet, payload.Length); packet.Write(payload); await stream.WriteAsync(packet.ToArray(), cancellationToken); }
    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    { var packetLength = await ReadVarIntAsync(stream, cancellationToken); if (packetLength is < 0 or > MaxPacketBytes) throw new InvalidDataException($"Server returned invalid packet length {packetLength}."); var payload = new byte[packetLength]; var read = 0; while (read < payload.Length) { var count = await stream.ReadAsync(payload.AsMemory(read), cancellationToken); if (count == 0) throw new EndOfStreamException("Server closed the status connection early."); read += count; } return payload; }
    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken cancellationToken)
    { var result = 0; var position = 0; var single = new byte[1]; while (position < 35) { var read = await stream.ReadAsync(single, cancellationToken); if (read == 0) throw new EndOfStreamException("Server closed the status connection early."); var current = single[0]; result |= (current & 0x7F) << position; if ((current & 0x80) == 0) return result; position += 7; } throw new InvalidDataException("VarInt is too long."); }
    private static int ReadVarInt(ReadOnlySpan<byte> buffer, ref int offset)
    { var result = 0; var position = 0; while (position < 35) { if (offset >= buffer.Length) throw new EndOfStreamException("Packet ended while reading VarInt."); var current = buffer[offset++]; result |= (current & 0x7F) << position; if ((current & 0x80) == 0) return result; position += 7; } throw new InvalidDataException("VarInt is too long."); }
    private static string ReadString(ReadOnlySpan<byte> buffer, ref int offset)
    { var byteLength = ReadVarInt(buffer, ref offset); if (byteLength is < 0 or > MaxStringBytes || offset + byteLength > buffer.Length) throw new InvalidDataException("Server returned an invalid status string length."); var value = Encoding.UTF8.GetString(buffer.Slice(offset, byteLength)); offset += byteLength; return value; }
    private static void WriteVarInt(Stream stream, int value)
    { var current = unchecked((uint)value); do { var temp = (byte)(current & 0x7F); current >>= 7; if (current != 0) temp |= 0x80; stream.WriteByte(temp); } while (current != 0); }
    private static void WriteString(Stream stream, string value)
    { var bytes = Encoding.UTF8.GetBytes(value); if (bytes.Length > ushort.MaxValue) throw new ArgumentException("Server host is too long.", nameof(value)); WriteVarInt(stream, bytes.Length); stream.Write(bytes); }

    private static ServerStatusResult ParseStatus(string json, string address)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Server status JSON root must be an object.");
        string? versionName = null; int? protocol = null;
        if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Object)
        { if (version.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) versionName = name.GetString(); if (version.TryGetProperty("protocol", out var protocolElement) && protocolElement.TryGetInt32(out var protocolValue)) protocol = protocolValue; }
        int? onlinePlayers = null; int? maxPlayers = null;
        if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Object)
        { if (players.TryGetProperty("online", out var online) && online.TryGetInt32(out var onlineValue)) onlinePlayers = onlineValue; if (players.TryGetProperty("max", out var max) && max.TryGetInt32(out var maxValue)) maxPlayers = maxValue; }
        var motd = root.TryGetProperty("description", out var description) ? FlattenText(description) : string.Empty;
        motd = LegacyFormattingRegex.Replace(motd, string.Empty).Trim();
        return new ServerStatusResult(ServerStatusState.Online, address, null, onlinePlayers, maxPlayers, versionName, protocol, motd, null);
    }
    private static string FlattenText(JsonElement element)
    { switch (element.ValueKind) { case JsonValueKind.String: return element.GetString() ?? string.Empty; case JsonValueKind.Array: return string.Concat(element.EnumerateArray().Select(FlattenText)); case JsonValueKind.Object: { var builder = new StringBuilder(); if (element.TryGetProperty("text", out var text)) builder.Append(FlattenText(text)); if (element.TryGetProperty("extra", out var extra)) builder.Append(FlattenText(extra)); if (builder.Length == 0 && element.TryGetProperty("translate", out var translate)) builder.Append(FlattenText(translate)); return builder.ToString(); } default: return string.Empty; } }
}
