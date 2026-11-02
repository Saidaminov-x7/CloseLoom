namespace CloseLoom.Infrastructure.Persistence;

public sealed record UserSettings
{
    public string Language { get; init; } = "en";
    public string Theme { get; init; } = "dark";
    public int GracePeriodSeconds { get; init; } = 3;
    public bool DryRun { get; init; } = true;
    public bool NotificationsEnabled { get; init; } = true;
    public string CloseBehavior { get; init; } = "ask";
    public long MaxLogBytes { get; init; } = 5 * 1024 * 1024;
    public IReadOnlyList<string> ProtectedExecutablePaths { get; init; } = Array.Empty<string>();
}

public interface IUserSettingsStore
{
    ValueTask<UserSettings> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);
}
