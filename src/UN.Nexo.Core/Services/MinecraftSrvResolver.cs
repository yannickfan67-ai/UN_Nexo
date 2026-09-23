using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace UN.Nexo.Core.Services;

public sealed record MinecraftSrvEndpoint(
    string Host,
    int Port,
    bool ServiceUnavailable = false);

public interface IMinecraftSrvResolver
{
    Task<MinecraftSrvEndpoint?> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default);
}

public sealed class MinecraftSrvResolver : IMinecraftSrvResolver
{
    internal const ushort SrvRecordType = 33;
    private const int MaxDnsMessageBytes = ushort.MaxValue;
    private static readonly TimeSpan PerDnsServerTimeout =
        TimeSpan.FromMilliseconds(500);

    public async Task<MinecraftSrvEndpoint?> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host)
            || IPAddress.TryParse(host, out _))
            return null;

        string asciiHost;
        try
        {
            asciiHost = new IdnMapping()
                .GetAscii(host.Trim().TrimEnd('.'));
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(asciiHost))
            return null;

        var queryName = "_minecraft._tcp." + asciiHost;
        byte[] query;
        ushort queryId;
        try
        {
            queryId = checked((ushort)Random.Shared.Next(ushort.MaxValue + 1));
            query = BuildQuery(queryId, queryName);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        foreach (var server in GetDnsServers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var perServer = new CancellationTokenSource(
                PerDnsServerTimeout);
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    perServer.Token);

            try
            {
                using var udp = new UdpClient(server.AddressFamily);
                udp.Connect(new IPEndPoint(server, 53));
                await udp.SendAsync(query, query.Length);
                var response = await udp.ReceiveAsync(linked.Token);

                var responseBytes = response.Buffer;
                if (IsTruncatedResponse(responseBytes, queryId))
                    responseBytes = await QueryTcpAsync(
                        server,
                        query,
                        linked.Token);

                var records = ParseResponse(
                    responseBytes,
                    queryId,
                    queryName);
                var selected = Select(records);
                if (selected is not null)
                    return selected;
            }
            catch (OperationCanceledException) when (
                perServer.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                // Try the next configured resolver.
            }
            catch (Exception ex) when (
                ex is SocketException
                or IOException
                or InvalidDataException)
            {
                // An unavailable or malformed resolver response is a soft
                // failure. Minecraft falls back to the original host/port.
            }
        }

        return null;
    }

    private static async Task<byte[]> QueryTcpAsync(
        IPAddress server,
        byte[] query,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(server.AddressFamily);
        await client.ConnectAsync(
            new IPEndPoint(server, 53),
            cancellationToken);
        await using var stream = client.GetStream();

        Span<byte> prefix = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(
            prefix,
            checked((ushort)query.Length));
        await stream.WriteAsync(
            prefix.ToArray(),
            cancellationToken);
        await stream.WriteAsync(
            query,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var lengthBytes = new byte[2];
        await ReadExactlyAsync(
            stream,
            lengthBytes,
            cancellationToken);
        var length =
            BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
        if (length is <= 0 or > MaxDnsMessageBytes)
            throw new InvalidDataException(
                "DNS-over-TCP response length is invalid.");

        var response = new byte[length];
        await ReadExactlyAsync(
            stream,
            response,
            cancellationToken);
        return response;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(
                buffer[read..],
                cancellationToken);
            if (count == 0)
                throw new EndOfStreamException(
                    "DNS-over-TCP response ended early.");
            read += count;
        }
    }

    internal static bool IsTruncatedResponse(
        ReadOnlySpan<byte> response,
        ushort expectedId)
    {
        if (response.Length < 4)
            throw new InvalidDataException(
                "DNS SRV response is truncated.");

        var id = BinaryPrimitives.ReadUInt16BigEndian(response);
        if (id != expectedId)
            throw new InvalidDataException(
                "DNS SRV response id does not match the query.");

        var flags =
            BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        if ((flags & 0x8000) == 0)
            throw new InvalidDataException(
                "DNS SRV response is not marked as a response.");

        return (flags & 0x0200) != 0;
    }

    private static IReadOnlyList<IPAddress> GetDnsServers()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network =>
                    network.OperationalStatus == OperationalStatus.Up)
                .SelectMany(network =>
                    network.GetIPProperties().DnsAddresses)
                .Where(address =>
                    address.AddressFamily
                        is AddressFamily.InterNetwork
                        or AddressFamily.InterNetworkV6)
                .Distinct()
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        catch (PlatformNotSupportedException)
        {
            return [];
        }
    }

    internal static byte[] BuildQuery(
        ushort queryId,
        string name)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(
            header,
            queryId);
        BinaryPrimitives.WriteUInt16BigEndian(
            header[2..],
            0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(
            header[4..],
            1);
        stream.Write(header);

        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is < 1 or > 63)
                throw new InvalidDataException(
                    "DNS SRV query contains an invalid label.");
            stream.WriteByte(checked((byte)bytes.Length));
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        Span<byte> question = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(
            question,
            SrvRecordType);
        BinaryPrimitives.WriteUInt16BigEndian(
            question[2..],
            1);
        stream.Write(question);

        if (stream.Length > 512)
            throw new InvalidDataException(
                "DNS SRV query is too large.");

        return stream.ToArray();
    }

    internal static IReadOnlyList<SrvRecord> ParseResponse(
        byte[] response,
        ushort expectedId,
        string expectedName)
    {
        if (response.Length < 12)
            throw new InvalidDataException(
                "DNS SRV response is truncated.");

        var data = response.AsSpan();
        var id = BinaryPrimitives.ReadUInt16BigEndian(data);
        if (id != expectedId)
            throw new InvalidDataException(
                "DNS SRV response id does not match the query.");

        var flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if ((flags & 0x8000) == 0)
            throw new InvalidDataException(
                "DNS SRV response is not marked as a response.");
        if ((flags & 0x0200) != 0)
            throw new InvalidDataException(
                "Truncated DNS SRV response requires TCP retry.");
        if ((flags & 0x000F) != 0)
            return [];

        var questionCount =
            BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        var answerCount =
            BinaryPrimitives.ReadUInt16BigEndian(data[6..]);

        if (questionCount != 1)
            throw new InvalidDataException(
                "DNS SRV response must contain exactly one question.");

        var offset = 12;
        var questionName = ReadName(data, ref offset);
        RequireRemaining(data, offset, 4);
        var questionType =
            BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        var questionClass =
            BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
        offset += 4;

        if (!DnsNameEquals(questionName, expectedName)
            || questionType != SrvRecordType
            || questionClass != 1)
            throw new InvalidDataException(
                "DNS SRV response question does not match the query.");

        var records = new List<SrvRecord>();
        for (var index = 0; index < answerCount; index++)
        {
            var owner = ReadName(data, ref offset);
            RequireRemaining(data, offset, 10);

            var type =
                BinaryPrimitives.ReadUInt16BigEndian(
                    data[offset..]);
            var recordClass =
                BinaryPrimitives.ReadUInt16BigEndian(
                    data[(offset + 2)..]);
            var dataLength =
                BinaryPrimitives.ReadUInt16BigEndian(
                    data[(offset + 8)..]);
            offset += 10;
            RequireRemaining(data, offset, dataLength);
            var recordEnd = checked(offset + dataLength);

            if (type == SrvRecordType
                && recordClass == 1
                && DnsNameEquals(owner, expectedName))
            {
                if (dataLength < 7)
                    throw new InvalidDataException(
                        "DNS SRV RDATA is too short.");

                var priority =
                    BinaryPrimitives.ReadUInt16BigEndian(
                        data[offset..]);
                var weight =
                    BinaryPrimitives.ReadUInt16BigEndian(
                        data[(offset + 2)..]);
                var port =
                    BinaryPrimitives.ReadUInt16BigEndian(
                        data[(offset + 4)..]);

                var targetOffset = offset + 6;
                var target = ReadName(
                    data,
                    ref targetOffset,
                    recordEnd);
                if (targetOffset != recordEnd)
                    throw new InvalidDataException(
                        "DNS SRV target does not consume its RDATA.");

                if (string.IsNullOrEmpty(target))
                {
                    records.Add(new SrvRecord(
                        priority,
                        weight,
                        port,
                        "."));
                }
                else if (port > 0)
                {
                    records.Add(new SrvRecord(
                        priority,
                        weight,
                        port,
                        target.TrimEnd('.')));
                }
            }

            offset = recordEnd;
        }

        return records;
    }

    internal static MinecraftSrvEndpoint? Select(
        IReadOnlyList<SrvRecord> records,
        Func<long, long>? chooseInclusive = null)
    {
        if (records.Count == 0)
            return null;

        var priority = records.Min(record => record.Priority);
        var eligible = records
            .Where(record => record.Priority == priority)
            .ToArray();

        if (eligible.Any(record =>
                string.Equals(record.Target, ".", StringComparison.Ordinal)))
            return new MinecraftSrvEndpoint(
                ".",
                0,
                ServiceUnavailable: true);

        var ordered = eligible
            .OrderBy(record => record.Weight == 0 ? 0 : 1)
            .ThenBy(record => record.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Port)
            .ToArray();

        var totalWeight =
            ordered.Sum(record => (long)record.Weight);

        SrvRecord selected;
        if (totalWeight == 0)
        {
            selected = ordered[0];
        }
        else
        {
            var choice = chooseInclusive is null
                ? Random.Shared.NextInt64(totalWeight + 1)
                : chooseInclusive(totalWeight);
            if (choice < 0 || choice > totalWeight)
                throw new ArgumentOutOfRangeException(
                    nameof(chooseInclusive),
                    "SRV weight selector returned an out-of-range value.");

            long running = 0;
            selected = ordered[^1];
            foreach (var record in ordered)
            {
                running += record.Weight;
                if (running >= choice)
                {
                    selected = record;
                    break;
                }
            }
        }

        return new MinecraftSrvEndpoint(
            selected.Target,
            selected.Port);
    }

    private static bool DnsNameEquals(
        string left,
        string right)
        => string.Equals(
            left.TrimEnd('.'),
            right.TrimEnd('.'),
            StringComparison.OrdinalIgnoreCase);

    internal static string ReadName(
        ReadOnlySpan<byte> data,
        ref int offset,
        int inlineEnd = -1)
    {
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var jumps = 0;
        var localEnd = inlineEnd < 0
            ? data.Length
            : inlineEnd;

        if (localEnd < 0 || localEnd > data.Length)
            throw new InvalidDataException(
                "DNS name boundary is invalid.");

        while (true)
        {
            RequireRemainingWithin(
                data,
                cursor,
                1,
                jumped ? data.Length : localEnd);
            var length = data[cursor++];

            if (length == 0)
            {
                if (!jumped)
                    offset = cursor;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                RequireRemainingWithin(
                    data,
                    cursor,
                    1,
                    jumped ? data.Length : localEnd);
                var pointer =
                    ((length & 0x3F) << 8)
                    | data[cursor++];
                if (pointer >= data.Length)
                    throw new InvalidDataException(
                        "DNS name compression pointer is invalid.");
                if (!jumped)
                    offset = cursor;
                cursor = pointer;
                jumped = true;
                if (++jumps > 32)
                    throw new InvalidDataException(
                        "DNS name compression loop detected.");
                continue;
            }

            if ((length & 0xC0) != 0
                || length > 63)
                throw new InvalidDataException(
                    "DNS label length is invalid.");

            RequireRemainingWithin(
                data,
                cursor,
                length,
                jumped ? data.Length : localEnd);
            labels.Add(
                Encoding.ASCII.GetString(
                    data.Slice(cursor, length)));
            cursor += length;
        }

        return string.Join('.', labels);
    }

    private static void RequireRemaining(
        ReadOnlySpan<byte> data,
        int offset,
        int count)
        => RequireRemainingWithin(
            data,
            offset,
            count,
            data.Length);

    private static void RequireRemainingWithin(
        ReadOnlySpan<byte> data,
        int offset,
        int count,
        int endExclusive)
    {
        if (endExclusive < 0
            || endExclusive > data.Length
            || offset < 0
            || count < 0
            || offset > endExclusive - count)
            throw new InvalidDataException(
                "DNS SRV response is truncated.");
    }

    internal sealed record SrvRecord(
        ushort Priority,
        ushort Weight,
        ushort Port,
        string Target);
}
