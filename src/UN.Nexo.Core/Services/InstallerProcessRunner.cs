using System.Diagnostics;

namespace UN.Nexo.Core.Services;

internal sealed record BoundedTextCapture(
    string Text,
    bool Truncated);

internal sealed record BoundedProcessCapture(
    int ExitCode,
    BoundedTextCapture StandardOutput,
    BoundedTextCapture StandardError);

internal static class BoundedProcessRunner
{
    private static readonly TimeSpan DrainShutdownTimeout =
        TimeSpan.FromSeconds(5);

    internal static async Task<BoundedProcessCapture> RunAsync(
        ProcessStartInfo startInfo,
        string displayName,
        int maxCharactersPerStream,
        CancellationToken cancellationToken,
        string truncationMarker =
            "[... process output truncated; retaining tail ...]\n")
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (maxCharactersPerStream <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCharactersPerStream),
                "Process output retention limit must be positive.");
        }

        if (!startInfo.RedirectStandardOutput
            || !startInfo.RedirectStandardError)
        {
            throw new InvalidOperationException(
                "Process output must be redirected for bounded capture.");
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
                truncationMarker,
                drainCancellation.Token);
        var stderrTask =
            CaptureReaderAsync(
                process.StandardError,
                maxCharactersPerStream,
                truncationMarker,
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

        return new BoundedProcessCapture(
            process.ExitCode,
            captures[0],
            captures[1]);
    }

    internal static async Task<BoundedTextCapture> CaptureReaderAsync(
        TextReader reader,
        int maxCharacters,
        string truncationMarker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(truncationMarker);
        if (maxCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCharacters),
                "Process output retention limit must be positive.");
        }

        var tail =
            new BoundedCharacterTail(
                maxCharacters,
                truncationMarker);
        var buffer =
            new char[Math.Min(4096, maxCharacters)];

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
            {
                process.Kill(
                    entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private sealed class BoundedCharacterTail(
        int capacity,
        string truncationMarker)
    {
        private readonly char[] _buffer =
            new char[capacity];
        private int _start;
        private int _count;
        private long _totalCharacters;

        internal void Append(
            ReadOnlySpan<char> value)
        {
            if (value.Length == 0)
                return;

            _totalCharacters +=
                value.Length;

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
                    _buffer[
                        (_start + _count)
                        % _buffer.Length] =
                        character;
                    _count++;
                }
                else
                {
                    _buffer[_start] =
                        character;
                    _start =
                        (_start + 1)
                        % _buffer.Length;
                }
            }
        }

        internal BoundedTextCapture Snapshot()
        {
            var ordered =
                new char[_count];
            for (var index = 0;
                 index < _count;
                 index++)
            {
                ordered[index] =
                    _buffer[
                        (_start + index)
                        % _buffer.Length];
            }

            var text =
                new string(ordered);
            if (_totalCharacters <= _buffer.Length)
            {
                return new BoundedTextCapture(
                    text,
                    Truncated: false);
            }

            if (truncationMarker.Length
                >= _buffer.Length)
            {
                return new BoundedTextCapture(
                    text,
                    Truncated: true);
            }

            var tailCapacity =
                _buffer.Length
                - truncationMarker.Length;
            if (text.Length > tailCapacity)
            {
                text =
                    text[^tailCapacity..];
            }

            return new BoundedTextCapture(
                truncationMarker + text,
                Truncated: true);
        }
    }
}

internal sealed record InstallerTextCapture(
    string Text,
    bool Truncated);

internal sealed record InstallerProcessCapture(
    int ExitCode,
    InstallerTextCapture StandardOutput,
    InstallerTextCapture StandardError);

internal static class InstallerProcessRunner
{
    private const string TruncatedMarker =
        "[... installer output truncated; retaining tail ...]\n";

    internal static async Task<InstallerProcessCapture> RunAsync(
        ProcessStartInfo startInfo,
        string displayName,
        int maxCharactersPerStream,
        CancellationToken cancellationToken)
    {
        var capture =
            await BoundedProcessRunner.RunAsync(
                startInfo,
                displayName,
                maxCharactersPerStream,
                cancellationToken,
                TruncatedMarker);

        return new InstallerProcessCapture(
            capture.ExitCode,
            new InstallerTextCapture(
                capture.StandardOutput.Text,
                capture.StandardOutput.Truncated),
            new InstallerTextCapture(
                capture.StandardError.Text,
                capture.StandardError.Truncated));
    }

    internal static async Task<InstallerTextCapture> CaptureReaderAsync(
        TextReader reader,
        int maxCharacters,
        CancellationToken cancellationToken = default)
    {
        var capture =
            await BoundedProcessRunner.CaptureReaderAsync(
                reader,
                maxCharacters,
                TruncatedMarker,
                cancellationToken);

        return new InstallerTextCapture(
            capture.Text,
            capture.Truncated);
    }
}
