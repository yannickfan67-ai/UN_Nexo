using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace UN.Nexo.Core.Services;

public sealed record MinecraftSrvEndpoint(
    string Host,
    int Port);

public interface IMinecraftSrvResolver
{
    Task<MinecraftSrvEndpoint?> ResolveAsync(
        string host,
        CancellationToken cancellationToken = default);
}

public sealed class MinecraftSrvResolver : IMinecraftSrvResolver
{
    private const ushort SrvRecordType = 33;
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

        byte[] query;
        ushort queryId;
        try
        {
            queryId = checked((ushort)Random.Shared.Next(ushort.MaxValue + 1));
            query = BuildQuery(
                queryId,
                "_minecraft._tcp." + asciiHost);
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
                var records = ParseResponse(
                    response.Buffer,
                    queryId);
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

    private static byte[] BuildQuery(
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

    private static IReadOnlyList<SrvRecord> ParseResponse(
        byte[] response,
        ushort expectedId)
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
        if ((flags & 0x000F) != 0)
            return [];

        var questionCount =
            BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        var answerCount =
            BinaryPrimitives.ReadUInt16BigEndian(data[6..]);

        var offset = 12;
        for (var index = 0; index < questionCount; index++)
        {
            _ = ReadName(data, ref offset);
            RequireRemaining(data, offset, 4);
            offset += 4;
        }

        var records = new List<SrvRecord>();
        for (var index = 0; index < answerCount; index++)
        {
            _ = ReadName(data, ref offset);
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
            var recordEnd = offset + dataLength;

            if (type == SrvRecordType
                && recordClass == 1
                && dataLength >= 7)
            {
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
                    ref targetOffset).TrimEnd('.');

                if (targetOffset <= recordEnd
                    && port > 0
                    && !string.IsNullOrWhiteSpace(target))
                {
                    records.Add(new SrvRecord(
                        priority,
                        weight,
                        port,
                        target));
                }
            }

            offset = recordEnd;
        }

        return records;
    }

    private static MinecraftSrvEndpoint? Select(
        IReadOnlyList<SrvRecord> records)
    {
        if (records.Count == 0)
            return null;

        var priority = records.Min(record => record.Priority);
        var eligible = records
            .Where(record => record.Priority == priority)
            .ToArray();
        var totalWeight = eligible.Sum(record => (int)record.Weight);

        SrvRecord selected;
        if (totalWeight <= 0)
        {
            selected = eligible
                .OrderBy(record => record.Target, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.Port)
                .First();
        }
        else
        {
            var choice = Random.Shared.Next(totalWeight);
            var running = 0;
            selected = eligible[^1];
            foreach (var record in eligible)
            {
                running += record.Weight;
                if (choice < running)
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

    private static string ReadName(
        ReadOnlySpan<byte> data,
        ref int offset)
    {
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var jumps = 0;

        while (true)
        {
            RequireRemaining(data, cursor, 1);
            var length = data[cursor++];

            if (length == 0)
            {
                if (!jumped)
                    offset = cursor;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                RequireRemaining(data, cursor, 1);
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

            RequireRemaining(data, cursor, length);
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
    {
        if (offset < 0
            || count < 0
            || offset > data.Length - count)
            throw new InvalidDataException(
                "DNS SRV response is truncated.");
    }

    private sealed record SrvRecord(
        ushort Priority,
        ushort Weight,
        ushort Port,
        string Target);
}
