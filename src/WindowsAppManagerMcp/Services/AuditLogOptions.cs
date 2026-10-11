namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Where the audit log of input and destructive actions (roadmap M2.17) is written, and how long its files are kept.
/// </summary>
/// <param name="Folder">The folder that holds one <c>&lt;yyyy-MM-dd&gt;.jsonl</c> file per local day.</param>
/// <param name="Retention">
/// How long a day's file is kept: the file of day D is deleted from day D + <paramref name="Retention"/> on, when the
/// server writes its first line of a day. Only files named like a day's file are ever deleted.
/// </param>
public sealed record AuditLogOptions(string Folder, TimeSpan Retention)
{
    /// <summary>
    /// Environment variable that overrides the audit folder. It should be an absolute path; environment variables
    /// inside it (e.g. <c>%USERPROFILE%</c>) are expanded. Tests point it, or <see cref="ForFolder"/>, at a temporary folder.
    /// </summary>
    public const string FolderEnvironmentVariable = "WAM_AUDIT_PATH";

    /// <summary>The audit folder's name under %LOCALAPPDATA%\WindowsAppManagerMcp.</summary>
    public const string FolderName = "audit";

    /// <summary>How long a day's file is kept by default: 7 days.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    /// <summary>Options for <paramref name="folder"/> with the default retention.</summary>
    public static AuditLogOptions ForFolder(string folder) => new(folder, DefaultRetention);

    /// <summary>
    /// Options for the running server: the folder from <see cref="FolderEnvironmentVariable"/> or
    /// <c>%LOCALAPPDATA%\WindowsAppManagerMcp\audit</c>. Reading them touches no file.
    /// </summary>
    public static AuditLogOptions FromEnvironment() => ForFolder(ResolveFolder(
        Environment.GetEnvironmentVariable(FolderEnvironmentVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    /// <summary>
    /// The override path (quotes stripped, environment variables expanded) when it is set, otherwise
    /// <c>&lt;localAppDataPath&gt;\WindowsAppManagerMcp\audit</c>.
    /// </summary>
    public static string ResolveFolder(string? overridePath, string localAppDataPath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Environment.ExpandEnvironmentVariables(overridePath.Trim().Trim('"'));
        }

        return Path.Combine(localAppDataPath ?? string.Empty, LayoutStorageOptions.AppDataFolderName, FolderName);
    }
}
