namespace CadTranslation.Core;

/// <summary>Resolves the job-owned path boundary for supported config layouts.</summary>
public static class JobPathPolicy
{
    public static string ResolveJobRoot(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        string fullConfigPath = Path.GetFullPath(configPath);
        string configDirectory = Path.GetDirectoryName(fullConfigPath)
            ?? throw new ArgumentException("Config path must have a parent directory.", nameof(configPath));

        if (!string.Equals(Path.GetFileName(configDirectory), "config", StringComparison.OrdinalIgnoreCase))
        {
            return configDirectory;
        }

        return Path.GetDirectoryName(configDirectory)
            ?? throw new ArgumentException("The config directory must have a parent job directory.", nameof(configPath));
    }
}
