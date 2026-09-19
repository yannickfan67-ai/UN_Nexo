using System.Diagnostics;

namespace UN.Nexo.Core.Services;

internal sealed record InstallerTextCapture(
    string Text,
    bool Truncated);

internal sealed record InstallerProcessCapture(
    int ExitCode,
    InstallerTextCapture StandardOutput,
    InstallerTextCapture StandardError);

internal static class InstallerProcessRunner
{
    private static readonly TimeSpan DrainShutdownTimeout =
        TimeSpan.FromSeconds(5);

    internal static async Task<InstallerProcessCapture> RunAsync(
        ProcessStartInfo startInfo,
        string displayName,
        int maxCharactersPerStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (maxCharactersPerStream <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCharactersPerStream),
                "Installer output retention limit must be positive.");
        }

        if (!startInfo.RedirectStandardOutput
            || !startInfo.RedirectStandardError)
        {
            throw new InvalidOperationException(
                "Installer process output must be redirected for bounded capture.");
        }

        using var process =
            new Process
            {
                StartInfo = startInfo
            };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Failed to start the {displayName} process.");
        }

        using var drainCancellation =
            new CancellationTokenSource();
        var stdoutTask =
            CaptureReaderAsync(
                process.StandardOutput,
                maxCharactersPerStream,
                drainCancellation.Token);
        var stderrTask =
            CaptureReaderAsync(
                process.StandardError,
                maxCharactersPerStream,
                drainCancellation.Token);

        try
        {
            await process.WaitForExitAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            drainCancellation.CancelAfter(
                DrainShutdownTimeout);
            try
            {
                await Task.WhenAll(
                    stdoutTask,
                    stderrTask);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            throw;
        }

        var captures =
            await Task.WhenAll(
                stdoutTask,
                stderrTask);

        return new InstallerProcessCapture(
            process.ExitCode,
            captures[0],
            captures[1]);
    }

    internal static async Task<InstallerTextCapture> CaptureReaderAsync(
        TextReader reader,
        int maxCharacters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maxCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCharacters),
                "Installer output retention limit must be positive.");
        }

        var tail = new BoundedCharacterTail(maxCharacters);
        var buffer = new char[Math.Min(4096, maxCharacters)];

        while (true)
        {
            var read =
                await reader.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken);
            if (read == 0)
                break;

            tail.Append(
                buffer.AsSpan(0, read));
        }

        return tail.Snapshot();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(
                    entireProcessTree: true);
        }
        catch
        {
        }
    }

    private sealed class BoundedCharacterTail(int capacity)
    {
        private const string TruncatedMarker =
            "[... installer output truncated; retaining tail ...]\n";

        private readonly char[] _buffer = new char[capacity];
        private int _start;
        private int _count;
        private long _totalCharacters;

        internal void Append(ReadOnlySpan<char> value)
        {
            if (value.Length == 0)
                return;

            _totalCharacters += value.Length;

            if (value.Length >= _buffer.Length)
            {
                value[^_buffer.Length..]
                    .CopyTo(_buffer);
                _start = 0;
                _count = _buffer.Length;
                return;
            }

            foreach (var character in value)
            {
                if (_count < _buffer.Length)
                {
                    _buffer[(_start + _count) % _buffer.Length] =
                        character;
                    _count++;
                }
                else
                {
                    _buffer[_start] = character;
                    _start = (_start + 1) % _buffer.Length;
                }
            }
        }

        internal InstallerTextCapture Snapshot()
        {
            var ordered = new char[_count];
            for (var index = 0; index < _count; index++)
            {
                ordered[index] =
                    _buffer[(_start + index) % _buffer.Length];
            }

            var text = new string(ordered);
            if (_totalCharacters <= _buffer.Length)
            {
                return new InstallerTextCapture(
                    text,
                    Truncated: false);
            }

            if (TruncatedMarker.Length >= _buffer.Length)
            {
                return new InstallerTextCapture(
                    text,
                    Truncated: true);
            }

            var tailCapacity =
                _buffer.Length - TruncatedMarker.Length;
            if (text.Length > tailCapacity)
                text = text[^tailCapacity..];

            return new InstallerTextCapture(
                TruncatedMarker + text,
                Truncated: true);
        }
    }
}
