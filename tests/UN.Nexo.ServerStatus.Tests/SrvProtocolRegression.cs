using System.Buffers.Binary;
using System.Text;
using UN.Nexo.Core.Services;

namespace UN.Nexo.ServerStatus.Tests;

internal static class SrvProtocolRegression
{
    public static Task RunAsync()
    {
        const ushort id = 0x4242;
        const string name = "_minecraft._tcp.play.example.test";

        var normal = BuildResponse(id, name, name, priority: 0, weight: 5, port: 25565, target: "backend.example.test");
        var records = MinecraftSrvResolver.ParseResponse(normal, id, name);
        Equal(1, records.Count, "normal SRV record count");
        Equal("backend.example.test", records[0].Target, "normal SRV target");

        var mismatchedQuestion = BuildResponse(id, "_minecraft._tcp.other.example.test", name, 0, 1, 25565, "backend.example.test");
        Throws<InvalidDataException>(
            () => MinecraftSrvResolver.ParseResponse(mismatchedQuestion, id, name),
            "mismatched SRV question");

        var mismatchedOwner = BuildResponse(id, name, "_minecraft._tcp.other.example.test", 0, 1, 25565, "backend.example.test");
        var unrelated = MinecraftSrvResolver.ParseResponse(mismatchedOwner, id, name);
        Equal(0, unrelated.Count, "unrelated SRV owner must be ignored");

        var unavailable = BuildResponse(id, name, name, 0, 0, 0, ".");
        var unavailableRecords = MinecraftSrvResolver.ParseResponse(unavailable, id, name);
        var unavailableEndpoint = MinecraftSrvResolver.Select(unavailableRecords);
        Equal(true, unavailableEndpoint?.ServiceUnavailable == true, "root SRV target must disable fallback");

        var weights = new[]
        {
            new MinecraftSrvResolver.SrvRecord(0, 1, 25565, "positive.example.test"),
            new MinecraftSrvResolver.SrvRecord(0, 0, 25566, "zero.example.test")
        };
        Equal("zero.example.test", MinecraftSrvResolver.Select(weights, _ => 0)?.Host,
            "RFC 2782 draw 0 must allow a zero-weight record");
        Equal("positive.example.test", MinecraftSrvResolver.Select(weights, total => total)?.Host,
            "RFC 2782 maximum draw must select the positive-weight record");

        var truncated = BuildHeaderOnly(id, flags: 0x8200);
        Equal(true, MinecraftSrvResolver.IsTruncatedResponse(truncated, id),
            "TC flag must trigger TCP retry");

        var malformedRdata = BuildMalformedTargetOverrun(id, name);
        Throws<InvalidDataException>(
            () => MinecraftSrvResolver.ParseResponse(malformedRdata, id, name),
            "SRV target inline encoding must stay within RDLENGTH");

        Console.WriteLine("[server-status] SRV protocol hardening");
        return Task.CompletedTask;
    }

    private static byte[] BuildResponse(
        ushort id,
        string questionName,
        string ownerName,
        ushort priority,
        ushort weight,
        ushort port,
        string target)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, id, 0x8180, questions: 1, answers: 1);
        WriteName(stream, questionName);
        WriteUInt16(stream, MinecraftSrvResolver.SrvRecordType);
        WriteUInt16(stream, 1);

        WriteName(stream, ownerName);
        WriteUInt16(stream, MinecraftSrvResolver.SrvRecordType);
        WriteUInt16(stream, 1);
        WriteUInt32(stream, 60);

        using var rdata = new MemoryStream();
        WriteUInt16(rdata, priority);
        WriteUInt16(rdata, weight);
        WriteUInt16(rdata, port);
        WriteName(rdata, target);
        var rdataBytes = rdata.ToArray();
        WriteUInt16(stream, checked((ushort)rdataBytes.Length));
        stream.Write(rdataBytes);
        return stream.ToArray();
    }

    private static byte[] BuildMalformedTargetOverrun(
        ushort id,
        string name)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, id, 0x8180, 1, 1);
        WriteName(stream, name);
        WriteUInt16(stream, MinecraftSrvResolver.SrvRecordType);
        WriteUInt16(stream, 1);

        // Owner compression pointer to the question at offset 12.
        stream.WriteByte(0xC0);
        stream.WriteByte(0x0C);
        WriteUInt16(stream, MinecraftSrvResolver.SrvRecordType);
        WriteUInt16(stream, 1);
        WriteUInt32(stream, 60);
        WriteUInt16(stream, 7);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 25565);
        stream.WriteByte(3); // label length is inside RDATA, bytes are not.
        stream.Write(Encoding.ASCII.GetBytes("bad"));
        stream.WriteByte(0);
        return stream.ToArray();
    }

    private static byte[] BuildHeaderOnly(ushort id, ushort flags)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, id, flags, 0, 0);
        return stream.ToArray();
    }

    private static void WriteHeader(
        Stream stream,
        ushort id,
        ushort flags,
        ushort questions,
        ushort answers)
    {
        WriteUInt16(stream, id);
        WriteUInt16(stream, flags);
        WriteUInt16(stream, questions);
        WriteUInt16(stream, answers);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
    }

    private static void WriteName(Stream stream, string name)
    {
        if (name == ".")
        {
            stream.WriteByte(0);
            return;
        }

        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            stream.WriteByte(checked((byte)bytes.Length));
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
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

        throw new InvalidOperationException($"{message}: expected {typeof(TException).Name}.");
    }
}
