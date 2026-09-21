namespace HearSay.SuperCollider.Validation;

/// <summary>Locates the SuperCollider language interpreter on this machine.</summary>
public sealed record SuperColliderInstallation(string SclangPath)
{
    /// <summary>Environment variable that overrides the search with the full path of the sclang executable.</summary>
    public const string SclangEnvironmentVariable = "HEARSAY_SCLANG";

    /// <summary>
    /// Finds sclang: first the <see cref="SclangEnvironmentVariable"/> variable, then the usual installation folders,
    /// then the PATH. Returns null when SuperCollider is not installed.
    /// </summary>
    public static SuperColliderInstallation? Find()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(SclangEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return new SuperColliderInstallation(fromEnvironment);
        }

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = Environment.GetFolderPath(folder);
                if (Directory.Exists(root))
                {
                    string[] installs = Directory.GetDirectories(root, "SuperCollider*");
                    Array.Sort(installs, StringComparer.OrdinalIgnoreCase);
                    Array.Reverse(installs);
                    foreach (string install in installs)
                    {
                        candidates.Add(Path.Combine(install, "sclang.exe"));
                    }
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/SuperCollider.app/Contents/MacOS/sclang");
        }

        string executable = OperatingSystem.IsWindows() ? "sclang.exe" : "sclang";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(directory, executable));
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return new SuperColliderInstallation(candidate);
            }
        }

        return null;
    }
}
