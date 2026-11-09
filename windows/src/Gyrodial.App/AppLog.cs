using System.Collections.Concurrent;
using System.IO;
using Gyrodial.Core;

namespace Gyrodial.App;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public override string ToString() =>
        $"{Time:HH:mm:ss.fff} {Level.ToString().ToUpperInvariant(),-5} [{Source}] {Message}";
}

/// <summary>
/// The app's log: a daily file in %AppData%\Gyrodial\logs (seven days kept) and the last 2000 lines in
/// memory for Settings → Log. Safe from any thread.
/// </summary>
public sealed class AppLog
{
    const int Capacity = 2000;
    static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    static readonly object InstanceGate = new();
    static AppLog? _instance;

    readonly ConcurrentQueue<LogEntry> _recent = new();
    readonly object _fileGate = new();
    readonly string? _dir;  // null: memory only
    StreamWriter? _writer;
    DateTime _writerDay;

    /// <summary>The log, writing to %AppData%\Gyrodial\logs unless <see cref="UseMemoryOnly"/> came first.</summary>
    public static AppLog Instance
    {
        get
        {
            lock (InstanceGate) return _instance ??= new AppLog(AppPaths.LogsDir);
        }
    }

    /// <summary>
    /// Keeps the log in memory only, for <c>--self-test</c> and <c>--render-ui</c>: their made-up dials
    /// and ports must not land in (or prune) the user's real log. Call before anything logs.
    /// </summary>
    public static void UseMemoryOnly()
    {
        lock (InstanceGate) _instance ??= new AppLog(null);
    }

    /// <summary>Raised on the writing thread after each entry.</summary>
    public event Action<LogEntry>? EntryAdded;

    AppLog(string? dir)
    {
        _dir = dir;
        if (dir is null) return;
        try
        {
            Directory.CreateDirectory(dir);
            // puck-*.log: logs from before 0.3.0, when the app was called Puck
            foreach (var f in Directory.EnumerateFiles(dir, "gyrodial-*.log").Concat(Directory.EnumerateFiles(dir, "puck-*.log")))
                if (DateTime.Now - File.GetLastWriteTime(f) > Keep) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public IReadOnlyList<LogEntry> Recent => _recent.ToArray();

    /// <summary>Where the files go (the temp folder for a memory-only log, so "Open folder" still opens something).</summary>
    public string Folder => _dir ?? Path.GetTempPath();

    public void Debug(string source, string message) => Write(LogLevel.Debug, source, message);
    public void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public void Warn(string source, string message) => Write(LogLevel.Warn, source, message);
    public void Error(string source, string message) => Write(LogLevel.Error, source, message);
    public void Error(string source, string message, Exception e) => Write(LogLevel.Error, source, $"{message}: {e}");

    public void Write(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message);
        _recent.Enqueue(entry);
        while (_recent.Count > Capacity && _recent.TryDequeue(out _)) { }

        // Debug lines (dial boot noise, BLE chatter) stay in memory only
        if (level != LogLevel.Debug && _dir is not null) WriteFile(entry);
        EntryAdded?.Invoke(entry);
    }

    void WriteFile(LogEntry entry)
    {
        lock (_fileGate)
        {
            try
            {
                if (_writer is null || _writerDay != entry.Time.Date)
                {
                    _writer?.Dispose();
                    _writerDay = entry.Time.Date;
                    _writer = new StreamWriter(new FileStream(Path.Combine(_dir!, $"gyrodial-{_writerDay:yyyyMMdd}.log"),
                        FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                }
                _writer.WriteLine(entry.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
