using System.Runtime.InteropServices;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Desktop.Diagnostics;

internal static class LauncherStartupTrace
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string? _path;

    public static string Path => _path ?? string.Empty;

    public static void Start()
    {
        lock (Gate)
        {
            if (_writer is not null)
                return;

            try
            {
                var root = new NexoPathService().GetDataRoot();
                var directory = System.IO.Path.Combine(root, "logs");
                Directory.CreateDirectory(directory);
                _path = System.IO.Path.Combine(directory, "startup-latest.log");
                _writer = CreateWriter(_path);
            }
            catch
            {
                try
                {
                    var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UN_Nexo");
                    Directory.CreateDirectory(directory);
                    _path = System.IO.Path.Combine(directory, "startup-latest.log");
                    _writer = CreateWriter(_path);
                }
                catch
                {
                    _writer = null;
                    _path = null;
                    return;
                }
            }

            WriteUnlocked($"[startup] Process started {DateTimeOffset.Now:O}");
            WriteUnlocked($"[startup] OS {RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.OSArchitecture} · process {RuntimeInformation.ProcessArchitecture}");
            WriteUnlocked($"[startup] Base directory {AppContext.BaseDirectory}");
            WriteUnlocked($"[startup] Current directory {Environment.CurrentDirectory}");
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
            WriteUnlocked(message);
    }

    public static void Failure(string stage, Exception exception)
    {
        Write($"[failure] {stage}: {exception.GetType().Name}: {exception.Message}");
        Write($"[failure] {exception.StackTrace ?? "No stack trace."}");
    }

    public static void Complete()
    {
        lock (Gate)
        {
            WriteUnlocked("[startup] Main window entered interactive state");
            try { _writer?.Flush(); } catch { }
        }
    }

    private static StreamWriter CreateWriter(string path)
        => new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };

    private static void WriteUnlocked(string message)
    {
        if (_writer is null)
            return;

        try
        {
            _writer.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}");
        }
        catch
        {
        }
    }
}
