using System.Globalization;
using System.Text;

namespace SoundBridge.Core.Diagnostics;

/// <summary>
/// Plain-text diagnostic log: one file per day, a bounded number of days kept, and a size cap per file.
/// Only connection events and errors are written. Audio never passes through this process, so none can be logged.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private const string Prefix = "soundbridge-";
    private const string Extension = ".log";

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly int _retainedFiles;
    private readonly long _maxBytesPerFile;

    private StreamWriter? _writer;
    private string? _openDay;
    private bool _disposed;

    public FileLog(string directory, TimeProvider? time = null, int retainedFiles = 7, long maxBytesPerFile = 2 * 1024 * 1024)
    {
        _directory = directory;
        _time = time ?? TimeProvider.System;
        _retainedFiles = Math.Max(1, retainedFiles);
        _maxBytesPerFile = Math.Max(1024, maxBytesPerFile);
    }

    public string Directory => _directory;

    public void Write(LogLevel level, string category, string message)
    {
        try
        {
            var now = _time.GetLocalNow();
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{now:yyyy-MM-dd'T'HH:mm:ss.fffzzz} {Label(level)} [{category}] {Flatten(message)}");

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var writer = WriterFor(now);
                writer.WriteLine(line);
                writer.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Diagnostics must never take the app down.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter WriterFor(DateTimeOffset now)
    {
        var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var path = Path.Combine(_directory, Prefix + day + Extension);

        if (_writer is not null && _openDay == day && _writer.BaseStream.Length < _maxBytesPerFile)
        {
            return _writer;
        }

        _writer?.Dispose();
        _writer = null;

        System.IO.Directory.CreateDirectory(_directory);
        if (File.Exists(path) && new FileInfo(path).Length >= _maxBytesPerFile)
        {
            // Keep one previous segment for the day; anything older is dropped.
            File.Move(path, path + ".old", overwrite: true);
        }

        if (_openDay != day)
        {
            Prune();
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _openDay = day;
        return _writer;
    }

    private void Prune()
    {
        var stale = System.IO.Directory
            .EnumerateFiles(_directory, Prefix + "*" + Extension)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(_retainedFiles - 1)
            .ToList();

        foreach (var file in stale)
        {
            File.Delete(file);
            File.Delete(file + ".old");
        }
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "INF",
    };

    private static string Flatten(string message) => message.ReplaceLineEndings(" | ");
}
