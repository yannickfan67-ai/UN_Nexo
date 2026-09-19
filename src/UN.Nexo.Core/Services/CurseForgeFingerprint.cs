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

        // CurseForge's file fingerprint is MurmurHash2 over the file bytes after
        // filtering ASCII whitespace bytes TAB/LF/CR/SPACE. The normalized bytes
        // are streamed so a large JAR does not need to be buffered in memory.
        uint normalizedLength = 0;
        uint hash = 0;
        var shift = 0;
        var partial = 0u;
        var buffer = new byte[128 * 1024];

        // MurmurHash2's initial state depends on normalized length. We cannot know
        // that before filtering, so retain normalized bytes in a bounded temp file
        // instead of the managed heap, then hash them in a second streaming pass.
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            "UN_Nexo",
            "curseforge-fingerprint-" + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);

        try
        {
            await using (var normalized = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous
                             | FileOptions.SequentialScan
                             | FileOptions.DeleteOnClose))
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;

                    for (var index = 0; index < read; index++)
                    {
                        var value = buffer[index];
                        if (IsWhitespace(value))
                            continue;

                        normalizedLength++;
                        await normalized.WriteAsync(
                            buffer.AsMemory(index, 1),
                            cancellationToken);
                    }
                }

                await normalized.FlushAsync(cancellationToken);
                normalized.Position = 0;
                hash = Seed ^ normalizedLength;

                while (true)
                {
                    var read = await normalized.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;

                    for (var index = 0; index < read; index++)
                    {
                        partial |= (uint)buffer[index] << shift;
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
            }

            if (shift > 0)
                hash = unchecked((hash ^ partial) * Multiplex);

            hash = unchecked((hash ^ (hash >> 13)) * Multiplex);
            hash ^= hash >> 15;
            return hash;
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
