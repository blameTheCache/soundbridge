using Microsoft.Extensions.Time.Testing;
using SoundBridge.Core.Diagnostics;

namespace SoundBridge.Core.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soundbridge-logs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Writes_one_timestamped_line_per_entry()
    {
        using (var log = new FileLog(_directory, _time))
        {
            log.Info("Connection", "Connected to 'Pixel'.");
            log.Error("Bluetooth", "first line\nsecond line");
        }

        var lines = File.ReadAllLines(Path.Combine(_directory, "soundbridge-20260301.log"));

        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-03-01T09:30:00.000+00:00 INF [Connection] Connected to 'Pixel'.", lines[0]);
        Assert.Equal("2026-03-01T09:30:00.000+00:00 ERR [Bluetooth] first line | second line", lines[1]);
    }

    [Fact]
    public void Starts_a_new_file_each_day_and_keeps_only_recent_ones()
    {
        using var log = new FileLog(_directory, _time, retainedFiles: 3);

        for (var day = 0; day < 6; day++)
        {
            log.Info("Test", $"day {day}");
            _time.Advance(TimeSpan.FromDays(1));
        }

        var files = Directory.GetFiles(_directory).Select(file => Path.GetFileName(file)).Order().ToArray();
        Assert.Equal(["soundbridge-20260304.log", "soundbridge-20260305.log", "soundbridge-20260306.log"], files);
    }

    [Fact]
    public void Rolls_a_file_that_exceeds_the_size_cap()
    {
        using var log = new FileLog(_directory, _time, maxBytesPerFile: 1024);

        for (var i = 0; i < 60; i++)
        {
            log.Info("Test", new string('x', 64));
        }

        var current = new FileInfo(Path.Combine(_directory, "soundbridge-20260301.log"));
        Assert.True(current.Length < 2048, $"Current segment is {current.Length} bytes.");
        Assert.True(File.Exists(current.FullName + ".old"));
    }

    [Fact]
    public void Writing_after_dispose_is_ignored()
    {
        var log = new FileLog(_directory, _time);
        log.Info("Test", "before");
        log.Dispose();

        log.Info("Test", "after");

        Assert.Single(File.ReadAllLines(Path.Combine(_directory, "soundbridge-20260301.log")));
    }
}
