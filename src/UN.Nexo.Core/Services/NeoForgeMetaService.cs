using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class NeoForgeMetaService(HttpClient httpClient)
{
    private const string MetadataUrl =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
    private const string MavenBase =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    private const int MaxMetadataBytes = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<InstallerLoaderVersion>> GetVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var gameVersion = RequireValue(
            minecraftVersion,
            nameof(minecraftVersion));
        using var response = await TrustedHttpDownload.SendGetAsync(
            httpClient,
            MetadataUrl,
            "NeoForge Maven metadata",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await ReadBoundedAsync(
            response.Content,
            MaxMetadataBytes,
            "NeoForge Maven metadata",
            cancellationToken);
        XDocument document;
        try
        {
            using var memory = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(
                memory,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxMetadataBytes
                });
            document = XDocument.Load(
                reader,
                LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(
                "NeoForge Maven metadata is malformed XML.",
                ex);
        }

        var matching = document
            .Descendants("version")
            .Select(element => element.Value.Trim())
            .Where(value =>
                value.Length > 0
                && IsCompatible(
                    gameVersion,
                    value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(
                value => ParseVersionParts(value),
                VersionPartsComparer.Instance)
            .Take(50)
            .Select((value, index) =>
                new InstallerLoaderVersion(
                    value,
                    Recommended: index == 0,
                    Latest: index == 0))
            .ToArray();

        return matching;
    }

    public static string GetLaunchVersionId(string loaderVersion)
        => "neoforge-" + RequireValue(
            loaderVersion,
            nameof(loaderVersion));

    public static string GetInstallerUrl(string loaderVersion)
    {
        var version = RequireValue(
            loaderVersion,
            nameof(loaderVersion));
        return $"{MavenBase}/{version}/neoforge-{version}-installer.jar";
    }

    internal static bool IsCompatible(
        string minecraftVersion,
        string loaderVersion)
    {
        var game = minecraftVersion
            .Split(
                '.',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);
        var loader = loaderVersion
            .Split(
                '.',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);
        if (game.Length < 2 || loader.Length < 3)
            return false;

        if (game[0] == "1")
        {
            if (!int.TryParse(
                    game[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var minor))
                return false;
            var patch = game.Length >= 3
                && int.TryParse(
                    game[2],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedPatch)
                    ? parsedPatch
                    : 0;
            return loader[0]
                       == minor.ToString(
                           CultureInfo.InvariantCulture)
                   && loader[1]
                       == patch.ToString(
                           CultureInfo.InvariantCulture);
        }

        if (loader[0] != game[0]
            || loader[1] != game[1])
            return false;

        if (game.Length >= 3)
            return loader[2] == game[2];

        return loader[2] == "0";
    }

    private static int[] ParseVersionParts(string value)
        => value.Split('.')
            .Select(part =>
                int.TryParse(
                    part,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : -1)
            .ToArray();

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maxBytes,
        string label,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declared
            && declared > maxBytes)
        {
            throw new InvalidDataException(
                $"{label} exceeds the {maxBytes}-byte limit.");
        }

        await using var input =
            await content.ReadAsStreamAsync(
                cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var total = 0;
        while (true)
        {
            var read = await input.ReadAsync(
                buffer,
                cancellationToken);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > maxBytes)
            {
                throw new InvalidDataException(
                    $"{label} exceeds the {maxBytes}-byte limit.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string RequireValue(
        string value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "A value is required.",
                parameterName);
        return value.Trim();
    }

    private sealed class VersionPartsComparer :
        IComparer<int[]>
    {
        internal static readonly VersionPartsComparer Instance =
            new();

        public int Compare(int[]? left, int[]? right)
        {
            left ??= [];
            right ??= [];
            var count = Math.Max(
                left.Length,
                right.Length);
            for (var index = 0;
                 index < count;
                 index++)
            {
                var a = index < left.Length
                    ? left[index]
                    : 0;
                var b = index < right.Length
                    ? right[index]
                    : 0;
                var comparison = a.CompareTo(b);
                if (comparison != 0)
                    return comparison;
            }
            return 0;
        }
    }
}
