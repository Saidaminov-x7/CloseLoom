namespace CloseLoom.Infrastructure.Persistence;

/// <summary>Resolves per-user data paths, with an explicit override for isolated runs and tests.</summary>
public static class CloseLoomDataPaths
{
    public const string OverrideEnvironmentVariable = "CLOSELOOM_DATA_DIR";

    public static string GetPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.GetFileName(fileName) != fileName)
            throw new ArgumentException("A data file name must not contain a directory path.", nameof(fileName));

        var overrideDirectory = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        var dataDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloseLoom")
            : Path.GetFullPath(overrideDirectory);

        return Path.Combine(dataDirectory, fileName);
    }
}
