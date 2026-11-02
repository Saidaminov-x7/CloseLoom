using CloseLoom.Infrastructure.Persistence;
using Xunit;

namespace CloseLoom.Infrastructure.Tests;

public sealed class JsonUserSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CloseLoom-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "settings.json");

    [Fact]
    public async Task MissingSettingsUseSafeDefaults()
    {
        var result = await new JsonUserSettingsStore(FilePath).LoadAsync();
        Assert.True(result.DryRun);
        Assert.Equal(3, result.GracePeriodSeconds);
    }

    [Fact]
    public async Task SettingsRoundTripAndClampUnsafeValues()
    {
        var store = new JsonUserSettingsStore(FilePath);
        await store.SaveAsync(new UserSettings { DryRun = false, GracePeriodSeconds = 0, Theme = "bad", Language = "ru" });
        var loaded = await store.LoadAsync();
        Assert.False(loaded.DryRun);
        Assert.Equal(3, loaded.GracePeriodSeconds);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal("ru", loaded.Language);
    }

    [Fact]
    public async Task MalformedJsonReturnsSafeDefaultsWithoutDeletingFile()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "{ broken");
        var result = await new JsonUserSettingsStore(FilePath).LoadAsync();
        Assert.True(result.DryRun);
        Assert.Equal("{ broken", await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task SaveWritesReadableJson()
    {
        var store = new JsonUserSettingsStore(FilePath);
        await store.SaveAsync(new UserSettings { Language = "ru" });
        Assert.Contains("\"Language\"", await File.ReadAllTextAsync(FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

public sealed class JsonApplicationAllowlistTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CloseLoom-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AddUpdateFindAndRemoveArePersistent()
    {
        Directory.CreateDirectory(_directory);
        var executable = Path.Combine(_directory, "allowed.exe");
        await File.WriteAllBytesAsync(executable, []);
        var store = new JsonApplicationAllowlist(Path.Combine(_directory, "apps.json"));
        var application = new CloseLoom.Core.Models.MonitoredApplication(Guid.NewGuid(), "Allowed", executable, GracePeriod: TimeSpan.FromSeconds(1));
        await store.AddOrUpdateAsync(application);
        var loaded = await store.FindExactExecutableAsync(executable, CancellationToken.None);
        Assert.Equal(application.Id, loaded?.Id);
        Assert.Equal(TimeSpan.FromSeconds(3), loaded?.GracePeriod);
        Assert.Single(await store.ListAsync());
        Assert.True(await store.RemoveAsync(application.Id));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task CorruptedAllowlistFailsClosedAsEmpty()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "apps.json");
        await File.WriteAllTextAsync(path, "{ invalid");
        Assert.Empty(await new JsonApplicationAllowlist(path).ListAsync());
    }

    [Fact]
    public async Task NullAllowlistEntryIsIgnoredWithoutCrashing()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "apps-with-null.json");
        await File.WriteAllTextAsync(path, "[null]");

        var entries = await new JsonApplicationAllowlist(path).ListAsync();

        Assert.Empty(entries);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
