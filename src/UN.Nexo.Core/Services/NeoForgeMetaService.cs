using System.Globalization;
using System.Text;
using System.Xml.Linq;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class NeoForgeMetaService(HttpClient httpClient)
{
    public const string MavenBaseUrl =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    public const string MetadataUrl =
        MavenBaseUrl + "/maven-metadata.xml";
    private const int MaxMetadataBytes = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<NeoForgeLoaderVersion>> GetVersionsAsync(
        string minecraftVersion,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var mc = NormalizeMinecraftVersion(minecraftVersion);
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "NeoForge version limit must be between 1 and 100.");

        using var response = await TrustedHttpDownload.SendGetAsync(
            httpClient,
            MetadataUrl,
            "NeoForge Maven metadata",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var xml = await ReadBoundedTextAsync(
            response.Content,
            MaxMetadataBytes,
            "NeoForge Maven metadata",
            cancellationToken);

        XDocument document;
        try
        {
            document = XDocument.Parse(
                xml,
                LoadOptions.None);
        }
        catch (Exception ex) when (
            ex is System.Xml.XmlException
            or ArgumentException)
        {
            throw new InvalidDataException(
                "NeoForge Maven metadata is not valid XML.",
                ex);
        }

        var versionElements = document
            .Root?
            .Element("versioning")?
            .Element("versions")?
            .Elements("version")
            .ToArray()
            ?? [];

        var values = new List<string>();
        foreach (var element in versionElements)
        {
            var value = element.Value.Trim();
            if (value.Length == 0
                || !IsSafeMavenVersion(value)
                || !IsCompatibleWithMinecraft(mc, value))
            {
                continue;
            }

            values.Add(value);
        }

        values = values
            .Distinct(StringComparer.Ordinal)
            .ToList();
        values.Sort(CompareVersionsDescending);

        return values
            .Take(limit)
            .Select((version, index) =>
                new NeoForgeLoaderVersion(
                    version,
                    IsLatest: index == 0,
                    IsPrerelease:
                        version.Contains(
                            '-',
                            StringComparison.Ordinal)))
            .ToArray();
    }

    public static string GetInstallerUrl(
        string minecraftVersion,
        string neoForgeVersion)
    {
        var mc = NormalizeMinecraftVersion(
            minecraftVersion);
        var neo = RequireNeoForgeVersion(
            neoForgeVersion);
        if (!IsCompatibleWithMinecraft(mc, neo))
        {
            throw new ArgumentException(
                $"NeoForge {neo} does not match Minecraft {mc}.",
                nameof(neoForgeVersion));
        }

        return
            $"{MavenBaseUrl}/{neo}/neoforge-{neo}-installer.jar";
    }

    public static string GetLaunchVersionId(
        string minecraftVersion,
        string neoForgeVersion)
    {
        var mc = NormalizeMinecraftVersion(
            minecraftVersion);
        var neo = RequireNeoForgeVersion(
            neoForgeVersion);
        if (!IsCompatibleWithMinecraft(mc, neo))
        {
            throw new ArgumentException(
                $"NeoForge {neo} does not match Minecraft {mc}.",
                nameof(neoForgeVersion));
        }

        return "neoforge-" + neo;
    }

    public static bool IsCompatibleWithMinecraft(
        string minecraftVersion,
        string neoForgeVersion)
    {
        var mc = NormalizeMinecraftVersion(
            minecraftVersion);
        if (!IsSafeMavenVersion(neoForgeVersion))
            return false;

        var mcParts = mc
            .Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numeric = new int[3];
        for (var index = 0;
             index < numeric.Length && index < mcParts.Length;
             index++)
        {
            if (!int.TryParse(
                    mcParts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out numeric[index]))
            {
                return false;
            }
        }

        string prefix;
        if (numeric[0] == 1)
        {
            if (mcParts.Length < 2)
                return false;
            prefix =
                numeric[1].ToString(CultureInfo.InvariantCulture)
                + "."
                + numeric[2].ToString(CultureInfo.InvariantCulture)
                + ".";
        }
        else if (numeric[0] >= 26)
        {
            if (mcParts.Length < 2)
                return false;
            prefix =
                numeric[0].ToString(CultureInfo.InvariantCulture)
                + "."
                + numeric[1].ToString(CultureInfo.InvariantCulture)
                + "."
                + numeric[2].ToString(CultureInfo.InvariantCulture)
                + ".";
        }
        else
        {
            return false;
        }

        return neoForgeVersion.StartsWith(
            prefix,
            StringComparison.Ordinal);
    }

    private static string NormalizeMinecraftVersion(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "Minecraft version is required.",
                nameof(value));

        var normalized = value.Trim();
        var parts = normalized.Split('.');
        if (parts.Length is < 2 or > 3
            || parts.Any(part =>
                part.Length == 0
                || !part.All(char.IsDigit)))
        {
            throw new ArgumentException(
                "NeoForge discovery currently supports release Minecraft versions in numeric major.minor[.patch] form.",
                nameof(value));
        }

        return normalized;
    }

    private static string RequireNeoForgeVersion(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "NeoForge version is required.",
                nameof(value));

        var normalized = value.Trim();
        if (!IsSafeMavenVersion(normalized))
            throw new ArgumentException(
                "NeoForge version contains unsupported characters.",
                nameof(value));
        return normalized;
    }

    private static bool IsSafeMavenVersion(
        string value)
        => value.Length is > 0 and <= 80
           && value.All(character =>
               char.IsLetterOrDigit(character)
               || character is '.' or '-' or '_' or '+');

    private static int CompareVersionsDescending(
        string left,
        string right)
        => -CompareVersions(left, right);

    private static int CompareVersions(
        string left,
        string right)
    {
        var leftStable =
            !left.Contains('-');
        var rightStable =
            !right.Contains('-');

        var leftNumbers = LeadingNumbers(left);
        var rightNumbers = LeadingNumbers(right);
        var count = Math.Max(
            leftNumbers.Length,
            rightNumbers.Length);
        for (var index = 0; index < count; index++)
        {
            var l = index < leftNumbers.Length
                ? leftNumbers[index]
                : 0;
            var r = index < rightNumbers.Length
                ? rightNumbers[index]
                : 0;
            var comparison = l.CompareTo(r);
            if (comparison != 0)
                return comparison;
        }

        var stableComparison =
            leftStable.CompareTo(rightStable);
        if (stableComparison != 0)
            return stableComparison;

        return string.Compare(
            left,
            right,
            StringComparison.OrdinalIgnoreCase);
    }

    private static int[] LeadingNumbers(
        string value)
    {
        var core = value.Split('-', 2)[0];
        return core
            .Split('.')
            .Select(part =>
                int.TryParse(
                    part,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : 0)
            .ToArray();
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        int maxBytes,
        string label,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > maxBytes)
        {
            throw new InvalidDataException(
                $"{label} exceeds the {maxBytes}-byte limit.");
        }

        await using var input =
            await content.ReadAsStreamAsync(cancellationToken);
        await using var output =
            new MemoryStream();
        var buffer = new byte[64 * 1024];
        var total = 0;

        while (true)
        {
            var read = await input.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);
            if (read == 0)
                break;

            total = checked(total + read);
            if (total > maxBytes)
            {
                throw new InvalidDataException(
                    $"{label} exceeds the {maxBytes}-byte limit.");
            }

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        return Encoding.UTF8.GetString(
            output.ToArray());
    }
}
