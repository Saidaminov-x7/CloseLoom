using System.Text.Json;
using AppGuard.Core.Interfaces;
using AppGuard.Core.Models;

namespace AppGuard.Infrastructure.Logging;

/// <summary>Append-only local event log with bounded retention. Never captures window content.</summary>
public sealed class JsonLinesActivityLog(string? filePath = null, long maxBytes = 5 * 1024 * 1024) : IActivityLog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppGuard", "activity.jsonl");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask WriteAsync(ActivityEvent activityEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("Log path has no parent directory.");
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(activityEvent, JsonOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(_filePath, line, cancellationToken).ConfigureAwait(false);
            if (new FileInfo(_filePath).Length > Math.Clamp(maxBytes, 64 * 1024, 100 * 1024 * 1024))
            {
                // Retain only newest half of configured capacity. Single-file rename avoids unbounded growth.
                var all = await File.ReadAllLinesAsync(_filePath, cancellationToken).ConfigureAwait(false);
                var keep = all.Reverse().Take(Math.Max(1, all.Length / 2)).Reverse();
                var temp = _filePath + ".tmp";
                await File.WriteAllLinesAsync(temp, keep, cancellationToken).ConfigureAwait(false);
                File.Move(temp, _filePath, overwrite: true);
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
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
