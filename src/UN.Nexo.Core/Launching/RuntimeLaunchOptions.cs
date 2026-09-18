using System.Text;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Launching;

public static class RuntimeLaunchOptions
{
    private static readonly string[] BlockedJvmOptions =
    [
        "-jar", "-cp", "-classpath", "--class-path", "-Djava.class.path",
        "-m", "--module"
    ];

    public static int RecommendMemoryMb(long availableBytes)
    {
        const long gib = 1024L * 1024L * 1024L;
        if (availableBytes <= 0)
            return 2048;
        if (availableBytes <= 3 * gib)
            return 1024;
        if (availableBytes <= 4 * gib)
            return 1536;
        if (availableBytes <= 8 * gib)
            return 3072;
        if (availableBytes <= 16 * gib)
            return 4096;
        if (availableBytes <= 32 * gib)
            return 6144;
        return 8192;
    }

    public static IReadOnlyList<string> Apply(
        IReadOnlyList<string> baseArguments,
        LauncherRuntimeSettings settings,
        long? availableBytes = null)
    {
        Validate(settings);
        var memoryMb = settings.MemoryMb == 0
            ? RecommendMemoryMb(availableBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)
            : settings.MemoryMb;
        var extra = ParseExtraJvmArguments(settings.ExtraJvmArguments);
        foreach (var argument in extra)
        {
            if (TryParseInitialHeapMb(argument, out var initialHeapMb) && initialHeapMb > memoryMb)
                throw new ArgumentException(
                    $"Initial heap ({initialHeapMb} MB) cannot exceed Nexo's maximum heap ({memoryMb} MB).",
                    nameof(settings));
        }

        var result = new List<string>(baseArguments.Count + extra.Count + 1)
        {
            $"-Xmx{memoryMb}M"
        };
        result.AddRange(extra);
        result.AddRange(baseArguments.Where(argument =>
            !argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)));
        return result;
    }

    public static void Validate(LauncherRuntimeSettings settings)
    {
        if (settings.MemoryMb != 0
            && settings.MemoryMb is < LauncherRuntimeSettings.MinMemoryMb or > LauncherRuntimeSettings.MaxMemoryMb)
            throw new ArgumentOutOfRangeException(
                nameof(settings.MemoryMb),
                $"Memory must be Auto (0) or between {LauncherRuntimeSettings.MinMemoryMb} and {LauncherRuntimeSettings.MaxMemoryMb} MB.");

        _ = ParseExtraJvmArguments(settings.ExtraJvmArguments);
    }

    public static IReadOnlyList<string> ParseExtraJvmArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var result = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';

        void Commit()
        {
            if (current.Length == 0)
                return;
            var value = current.ToString();
            current.Clear();
            ValidateJvmArgument(value);
            result.Add(value);
        }

        foreach (var character in text)
        {
            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                else
                    current.Append(character);
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                Commit();
                continue;
            }

            current.Append(character);
        }

        if (quote != '\0')
            throw new ArgumentException("Extra JVM arguments contain an unterminated quote.", nameof(text));
        Commit();
        return result;
    }

    private static bool TryParseInitialHeapMb(string value, out long memoryMb)
    {
        memoryMb = 0;
        if (!value.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) || value.Length <= 4)
            return false;

        var text = value.AsSpan(4);
        long multiplier = 1;
        if (char.IsLetter(text[^1]))
        {
            multiplier = char.ToUpperInvariant(text[^1]) switch
            {
                'K' => 1,
                'M' => 1024,
                'G' => 1024L * 1024,
                'T' => 1024L * 1024 * 1024,
                _ => 0
            };
            if (multiplier == 0)
                return false;
            text = text[..^1];
        }

        if (!long.TryParse(text, out var amount) || amount < 0)
            return false;
        try
        {
            var kib = checked(amount * multiplier);
            memoryMb = (kib + 1023) / 1024;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void ValidateJvmArgument(string value)
    {
        // Files are expanded by Java after Nexo's validation and can contain
        // heap/classpath overrides or an entirely different entry point.
        if (value.StartsWith("@", StringComparison.Ordinal)
            || value.StartsWith("-XX:Flags=", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("-XX:VMOptionsFile=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("JVM option files are not supported. Enter JVM options directly so Nexo can validate them.");
        if (!value.StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException($"Extra JVM argument '{value}' must be a JVM option.");
        if (value.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("-XX:MaxHeapSize=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use Nexo's memory setting instead of adding -Xmx manually.");
        if (BlockedJvmOptions.Any(option => string.Equals(value, option, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"'{value}' is managed by the launcher and cannot be overridden here.");
    }
}
