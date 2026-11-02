using System.Text.Json;

namespace CloseLoom.Infrastructure.Persistence;

public sealed class JsonUserSettingsStore(string? filePath = null) : IUserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _filePath = filePath ?? CloseLoomDataPaths.GetPath("settings.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<UserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath)) return new UserSettings();
            try
            {
                await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var result = await JsonSerializer.DeserializeAsync<UserSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                return Sanitize(result ?? new UserSettings());
            }
            catch (JsonException) { return new UserSettings(); }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temp = _filePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("Settings path has no parent directory.");
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, Sanitize(settings), JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            _gate.Release();
        }
    }

    private static UserSettings Sanitize(UserSettings settings) => settings with
    {
        Language = settings.Language is "ru" or "en" ? settings.Language : "en",
        Theme = settings.Theme is "light" or "dark" ? settings.Theme : "dark",
        GracePeriodSeconds = Math.Clamp(settings.GracePeriodSeconds, 3, 300),
        MaxLogBytes = Math.Clamp(settings.MaxLogBytes, 64 * 1024, 100 * 1024 * 1024),
        CloseBehavior = settings.CloseBehavior is "tray" or "exit" or "ask" ? settings.CloseBehavior : "ask",
        ProtectedExecutablePaths = settings.ProtectedExecutablePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>()
    };
}
