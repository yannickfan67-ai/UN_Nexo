using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace UN.Nexo.Core.Launching;

public sealed record MinecraftServerTarget(
    string Host,
    int Port,
    bool HasExplicitPort = true)
{
    public const int DefaultPort = 25565;

    public string Authority => Host.Contains(':')
        ? $"[{Host}]:{Port}"
        : $"{Host}:{Port}";

    public static MinecraftServerTarget Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Enter a server address.", nameof(value));

        var text = value.Trim();
        if (text.Contains('/') || text.Contains('\\') || text.Any(char.IsControl))
            throw new FormatException("Server address contains invalid characters.");

        string host;
        var port = DefaultPort;
        var hasExplicitPort = false;

        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            var closing = text.IndexOf(']');
            if (closing <= 1)
                throw new FormatException("Invalid IPv6 server address.");

            host = text[1..closing];
            if (!IPAddress.TryParse(host, out var address)
                || address.AddressFamily != AddressFamily.InterNetworkV6)
                throw new FormatException("Bracketed server addresses must contain an IPv6 literal.");

            var remainder = text[(closing + 1)..];
            if (remainder.Length > 0)
            {
                if (!remainder.StartsWith(":", StringComparison.Ordinal)
                    || !TryParsePort(remainder[1..], out port))
                    throw new FormatException("Invalid server port.");
                hasExplicitPort = true;
            }
        }
        else
        {
            var colonCount = text.Count(character => character == ':');
            if (colonCount == 1)
            {
                var separator = text.LastIndexOf(':');
                host = text[..separator];
                if (!TryParsePort(text[(separator + 1)..], out port))
                    throw new FormatException("Invalid server port.");
                hasExplicitPort = true;
            }
            else
            {
                // Multiple colons without brackets are treated as a raw IPv6 host using the default port.
                host = text;
            }
        }

        host = host.Trim();
        if (host.Length == 0 || host.Length > 253 || host.Any(char.IsWhiteSpace))
            throw new FormatException("Invalid server host.");

        return new MinecraftServerTarget(
            host,
            port,
            hasExplicitPort);
    }

    private static bool TryParsePort(string text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
           && port is >= 1 and <= 65535;
}
