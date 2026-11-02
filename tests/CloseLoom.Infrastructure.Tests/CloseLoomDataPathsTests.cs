using CloseLoom.Infrastructure.Persistence;
using Xunit;

namespace CloseLoom.Infrastructure.Tests;

public sealed class CloseLoomDataPathsTests
{
    [Fact]
    public void ExplicitDataDirectoryOverrideRoutesAllDefaultStoresToIsolatedFolder()
    {
        var previous = Environment.GetEnvironmentVariable(CloseLoomDataPaths.OverrideEnvironmentVariable);
        var isolatedDirectory = Path.Combine(Path.GetTempPath(), "CloseLoom-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(CloseLoomDataPaths.OverrideEnvironmentVariable, isolatedDirectory);

            Assert.Equal(Path.Combine(isolatedDirectory, "settings.json"), CloseLoomDataPaths.GetPath("settings.json"));
            Assert.Equal(Path.Combine(isolatedDirectory, "applications.json"), CloseLoomDataPaths.GetPath("applications.json"));
            Assert.Equal(Path.Combine(isolatedDirectory, "activity.jsonl"), CloseLoomDataPaths.GetPath("activity.jsonl"));
            Assert.False(Directory.Exists(isolatedDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CloseLoomDataPaths.OverrideEnvironmentVariable, previous);
        }
    }
}
