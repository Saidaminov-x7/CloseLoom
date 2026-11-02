using System.Text.Json;
using System.Text;
using CloseLoom.Core.Interfaces;
using CloseLoom.Core.Models;
using CloseLoom.Infrastructure.Persistence;

namespace CloseLoom.Infrastructure.Logging;

/// <summary>Append-only local event log with bounded retention. Never captures window content.</summary>
public sealed class JsonLinesActivityLog(string? filePath = null, long maxBytes = 5 * 1024 * 1024) : IActivityLog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _filePath = filePath ?? CloseLoomDataPaths.GetPath("activity.jsonl");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _maxBytes = Math.Clamp(maxBytes, 64 * 1024, 100 * 1024 * 1024);

    public long MaxBytes
    {
        get => Interlocked.Read(ref _maxBytes);
        set => Interlocked.Exchange(ref _maxBytes, Math.Clamp(value, 64 * 1024, 100 * 1024 * 1024));
    }

    public async ValueTask WriteAsync(ActivityEvent activityEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("Log path has no parent directory.");
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(activityEvent, JsonOptions) + Environment.NewLine;
            var limit = MaxBytes;
            if (Encoding.UTF8.GetByteCount(line) > limit)
                throw new InvalidOperationException("A single activity entry exceeds the log size limit.");
            await File.AppendAllTextAsync(_filePath, line, cancellationToken).ConfigureAwait(false);
            if (new FileInfo(_filePath).Length > limit)
            {
                var all = await File.ReadAllLinesAsync(_filePath, cancellationToken).ConfigureAwait(false);
                var keep = new List<string>();
                long retainedBytes = 0;
                foreach (var entry in all.Reverse())
                {
                    var bytes = Encoding.UTF8.GetByteCount(entry + Environment.NewLine);
                    if (retainedBytes + bytes > limit) break;
                    keep.Add(entry);
                    retainedBytes += bytes;
                }
                keep.Reverse();
                var temp = _filePath + ".tmp";
                await File.WriteAllLinesAsync(temp, keep, cancellationToken).ConfigureAwait(false);
                File.Move(temp, _filePath, overwrite: true);
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<ActivityEvent>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath)) return Array.Empty<ActivityEvent>();
            var results = new List<ActivityEvent>();
            using var reader = new StreamReader(_filePath, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ActivityEvent>(line, JsonOptions);
                    if (entry is not null) results.Add(entry);
                }
                catch (JsonException) { /* Ignore a damaged entry without hiding the rest. */ }
            }
            return results;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (string.Equals(Path.GetFullPath(destinationPath), Path.GetFullPath(_filePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The export destination must differ from the live activity log.", nameof(destinationPath));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_filePath))
            {
                await using var source = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            else
                await File.WriteAllTextAsync(destinationPath, string.Empty, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
