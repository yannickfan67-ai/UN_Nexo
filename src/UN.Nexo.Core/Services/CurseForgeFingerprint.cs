namespace UN.Nexo.Core.Services;

internal static class CurseForgeFingerprint
{
    private const uint Multiplex = 0x5BD1E995;
    private const uint Seed = 1;

    internal static async Task<uint> ComputeAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("Fingerprint input must be readable.", nameof(stream));

        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            "UN_Nexo",
            "curseforge-fingerprint-" + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);

        uint normalizedLength = 0;
        var inputBuffer = new byte[128 * 1024];
        var normalizedBuffer = new byte[inputBuffer.Length];

        try
        {
            await using (var normalized = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous
                             | FileOptions.SequentialScan
                             | FileOptions.DeleteOnClose))
            {
                while (true)
                {
                    var read = await stream.ReadAsync(inputBuffer, cancellationToken);
                    if (read == 0)
                        break;

                    var writeCount = 0;
                    for (var index = 0; index < read; index++)
                    {
                        var value = inputBuffer[index];
                        if (!IsWhitespace(value))
                            normalizedBuffer[writeCount++] = value;
                    }

                    if (writeCount == 0)
                        continue;
                    normalizedLength = checked(normalizedLength + (uint)writeCount);
                    await normalized.WriteAsync(
                        normalizedBuffer.AsMemory(0, writeCount),
                        cancellationToken);
                }

                await normalized.FlushAsync(cancellationToken);
                normalized.Position = 0;

                var hash = Seed ^ normalizedLength;
                uint partial = 0;
                var shift = 0;

                while (true)
                {
                    var read = await normalized.ReadAsync(inputBuffer, cancellationToken);
                    if (read == 0)
                        break;

                    for (var index = 0; index < read; index++)
                    {
                        partial |= (uint)inputBuffer[index] << shift;
                        shift += 8;
                        if (shift != 32)
                            continue;

                        var mixed = unchecked(partial * Multiplex);
                        mixed = unchecked((mixed ^ (mixed >> 24)) * Multiplex);
                        hash = unchecked(hash * Multiplex) ^ mixed;
                        partial = 0;
                        shift = 0;
                    }
                }

                if (shift > 0)
                    hash = unchecked((hash ^ partial) * Multiplex);

                hash = unchecked((hash ^ (hash >> 13)) * Multiplex);
                hash ^= hash >> 15;
                return hash;
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static bool IsWhitespace(byte value)
        => value is 9 or 10 or 13 or 32;
}
