using AppGuard.Core.Models;
using AppGuard.Infrastructure.Logging;
using Xunit;

namespace AppGuard.Infrastructure.Tests;

public sealed class ActivityLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AppGuard-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadKeepsValidEventsWhenOneLineIsDamaged()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "events.jsonl");
        var log = new JsonLinesActivityLog(path);
        var entry = new ActivityEvent(DateTimeOffset.UtcNow, "Editor", ActivityEventType.WindowClosed, "Observed");
        await log.WriteAsync(entry, CancellationToken.None);
        await File.AppendAllTextAsync(path, "invalid json" + Environment.NewLine);
        Assert.Equal(entry, Assert.Single(await log.ReadAsync()));
    }

    [Fact]
    public async Task ExportCannotOverwriteLiveLog()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "events.jsonl");
        var log = new JsonLinesActivityLog(path);
        var entry = new ActivityEvent(DateTimeOffset.UtcNow, "Editor", ActivityEventType.ProcessSkipped, "Skipped");
        await log.WriteAsync(entry, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(async () => await log.ExportAsync(path));
        Assert.Equal(entry, Assert.Single(await log.ReadAsync()));
    }

    [Fact]
    public async Task RetentionRespectsByteLimitForLargeEntries()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "events.jsonl");
        var log = new JsonLinesActivityLog(path, 65536);
        for (var index = 0; index < 4; index++)
            await log.WriteAsync(new ActivityEvent(DateTimeOffset.UtcNow, "Editor", ActivityEventType.ProcessSkipped, index.ToString(), new string('a', 30000)), CancellationToken.None);
        Assert.InRange(new FileInfo(path).Length, 1, 65536);
        Assert.Equal("3", (await log.ReadAsync()).Last().Result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
