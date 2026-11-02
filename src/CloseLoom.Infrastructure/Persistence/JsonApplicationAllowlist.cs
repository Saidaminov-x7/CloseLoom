using System.Text.Json;
using CloseLoom.Core.Interfaces;
using CloseLoom.Core.Models;

namespace CloseLoom.Infrastructure.Persistence;

/// <summary>Persists only explicitly selected executable paths. Corrupt data fails closed as an empty list.</summary>
public sealed class JsonApplicationAllowlist(string? filePath = null) : IApplicationStore, IAllowlist
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloseLoom", "applications.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<IReadOnlyList<MonitoredApplication>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask<MonitoredApplication?> FindExactExecutableAsync(string executablePath, CancellationToken cancellationToken)
    {
        var normalized = Normalize(executablePath);
        var items = await ListAsync(cancellationToken).ConfigureAwait(false);
        return items.FirstOrDefault(app => PathEquals(app.ExecutablePath, normalized));
    }

    public async ValueTask AddOrUpdateAsync(MonitoredApplication application, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        var path = Normalize(application.ExecutablePath);
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Allowlist entries must point to an existing EXE file.", nameof(application));
        if (application.IsProtected) throw new ArgumentException("Protected applications cannot be added to the allowlist.", nameof(application));
        var normalized = application with { ExecutablePath = path, GracePeriod = TimeSpan.FromSeconds(Math.Clamp((int)application.EffectiveGracePeriod.TotalSeconds, 3, 300)) };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            current.RemoveAll(app => app.Id == normalized.Id || PathEquals(app.ExecutablePath, path));
            current.Add(normalized);
            await SaveCoreAsync(current, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var count = current.RemoveAll(app => app.Id == id);
            if (count > 0) await SaveCoreAsync(current, cancellationToken).ConfigureAwait(false);
            return count > 0;
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<IReadOnlyList<MonitoredApplication>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return Array.Empty<MonitoredApplication>();
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var values = await JsonSerializer.DeserializeAsync<List<MonitoredApplication>>(stream, Options, cancellationToken).ConfigureAwait(false);
            return (values ?? []).Where(app => app is not null && app.Id != Guid.Empty && !app.IsProtected && !string.IsNullOrWhiteSpace(app.ExecutablePath))
                .Select(app => app with { ExecutablePath = Normalize(app.ExecutablePath), GracePeriod = TimeSpan.FromSeconds(Math.Clamp((int)app.EffectiveGracePeriod.TotalSeconds, 3, 300)) })
                .ToArray();
        }
        catch (JsonException) { return Array.Empty<MonitoredApplication>(); }
        catch (ArgumentException) { return Array.Empty<MonitoredApplication>(); }
    }

    private async ValueTask SaveCoreAsync(IReadOnlyList<MonitoredApplication> applications, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("Allowlist path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = _filePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, applications, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool PathEquals(string left, string right)
    { try { return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; } }
}
