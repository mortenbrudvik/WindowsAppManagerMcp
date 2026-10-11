using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The <c>window</c> argument of the tools that act on one window (roadmap M2.4): which window to act on, resolved by
/// IWindowService.ResolveWindow to exactly one window, or refused (WindowNotFound, AmbiguousWindow with candidates,
/// HandleMismatch, InvalidHandle). It never silently picks the first of several matches.
/// </summary>
/// <remarks>
/// <para>
/// Without <see cref="Handle"/> and <see cref="Foreground"/> the filters run as a find_windows query
/// (<see cref="WindowQuery"/>): only visible user-facing windows, unless <see cref="ClassName"/> names a class, or
/// <see cref="IncludeCloaked"/> or <see cref="IncludeUntitled"/> relax the listing. <see cref="Index"/> picks one of
/// several matches in z-order (0 is the front one).
/// </para>
/// <para>
/// With <see cref="Handle"/>, the other filters are what that window must still be: a window whose process, title or
/// class no longer matches is refused with HandleMismatch, which guards against a handle that closed and was reused by
/// another window. With <see cref="Foreground"/>, the filters are checked against the window in front.
/// </para>
/// Unknown properties are refused (InvalidArguments), so a misspelled filter never widens the match.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WindowSelector
{
    /// <summary>One window, by handle; the other filters then check that it is still that window.</summary>
    public long? Handle { get; init; }

    /// <summary>Part of the title, case-insensitive.</summary>
    public string? TitleContains { get; init; }

    /// <summary>The whole title, case-insensitive.</summary>
    public string? TitleEquals { get; init; }

    /// <summary>A .NET regular expression the title must match, case-insensitive (100 ms timeout).</summary>
    public string? TitleRegex { get; init; }

    /// <summary>The whole process name, case-insensitive, with or without .exe (part of it with <see cref="ProcessNameContains"/>).</summary>
    public string? ProcessName { get; init; }

    /// <summary>Match <see cref="ProcessName"/> as a part of the process name.</summary>
    public bool ProcessNameContains { get; init; }

    /// <summary>The process ID (for a UWP app, the app's or its host's).</summary>
    public int? ProcessId { get; init; }

    /// <summary>The exact window class name, case-insensitive.</summary>
    public string? ClassName { get; init; }

    /// <summary>The window in the foreground; the other filters must match it.</summary>
    public bool Foreground { get; init; }

    /// <summary>Which of several matches, in z-order (0 is the front one).</summary>
    public int? Index { get; init; }

    /// <summary>Also match cloaked windows (other virtual desktops, suspended apps).</summary>
    public bool IncludeCloaked { get; init; }

    /// <summary>Also match untitled windows.</summary>
    public bool IncludeUntitled { get; init; }

    /// <summary>True when a filter beside <see cref="Handle"/> is set: what the window must be, or which windows match.</summary>
    [JsonIgnore]
    public bool HasFilters =>
        !string.IsNullOrEmpty(TitleContains) || !string.IsNullOrEmpty(TitleEquals) || !string.IsNullOrEmpty(TitleRegex) ||
        !string.IsNullOrEmpty(ProcessName) || ProcessId.HasValue || !string.IsNullOrEmpty(ClassName);

    /// <summary>
    /// True when the selector only names a handle: it is used as that handle, exactly as the tool's handle argument
    /// (the tool's own checks report a handle that is not a window).
    /// </summary>
    [JsonIgnore]
    public bool IsHandleOnly => Handle.HasValue && !HasFilters && !Foreground && !Index.HasValue;

    /// <summary>The query that lists the windows the filters match (not for <see cref="Handle"/> or <see cref="Foreground"/>).</summary>
    public WindowQuery ToQuery() => new()
    {
        TitleContains = TitleContains,
        TitleEquals = TitleEquals,
        TitleRegex = TitleRegex,
        ProcessName = ProcessName,
        ProcessNameContains = ProcessNameContains,
        ProcessId = ProcessId,
        ClassName = ClassName,
        IncludeCloaked = IncludeCloaked,
        IncludeUntitled = IncludeUntitled,
        VisibleOnly = true,
        IncludeMinimized = true
    };

    /// <summary>The filters that are set, for messages: "processName 'chrome', titleContains 'Inbox'".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Handle is { } handle)
            parts.Add($"handle {handle}");
        if (Foreground)
            parts.Add("foreground");
        if (!string.IsNullOrEmpty(TitleContains))
            parts.Add($"titleContains '{TitleContains}'");
        if (!string.IsNullOrEmpty(TitleEquals))
            parts.Add($"titleEquals '{TitleEquals}'");
        if (!string.IsNullOrEmpty(TitleRegex))
            parts.Add($"titleRegex '{TitleRegex}'");
        if (!string.IsNullOrEmpty(ProcessName))
            parts.Add(ProcessNameContains ? $"processName containing '{ProcessName}'" : $"processName '{ProcessName}'");
        if (ProcessId is { } processId)
            parts.Add($"processId {processId}");
        if (!string.IsNullOrEmpty(ClassName))
            parts.Add($"className '{ClassName}'");
        if (Index is { } index)
            parts.Add($"index {index}");
        if (IncludeCloaked)
            parts.Add("includeCloaked");
        if (IncludeUntitled)
            parts.Add("includeUntitled");
        return parts.Count == 0 ? "no filter" : string.Join(", ", parts);
    }
}

/// <summary>
/// What IWindowService.ResolveWindow found for a <see cref="WindowSelector"/>: exactly one window, or why not, with the
/// windows that matched when there were several (at most <see cref="MaxCandidates"/>).
/// </summary>
public sealed record WindowResolution
{
    /// <summary>The most candidates an ambiguous or out-of-range selector reports.</summary>
    public const int MaxCandidates = 5;

    /// <summary>True when exactly one window was found.</summary>
    public bool Success { get; init; }

    /// <summary>The window found; zero when none was.</summary>
    public nint Handle { get; init; }

    /// <summary>The window found, as it was read; null when none was.</summary>
    public WindowInfo? Window { get; init; }

    /// <summary>WindowNotFound, AmbiguousWindow, HandleMismatch, InvalidHandle or InvalidArguments; null on success.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Why no single window was found, and what to do; null on success.</summary>
    public string? Error { get; init; }

    /// <summary>Up to <see cref="MaxCandidates"/> of the matching windows, in z-order, when no single one was chosen.</summary>
    public IReadOnlyList<WindowInfo> Candidates { get; init; } = [];

    /// <summary>How many windows matched the filters.</summary>
    public int MatchCount { get; init; }

    /// <summary>The one window found.</summary>
    public static WindowResolution Found(WindowInfo window) =>
        new() { Success = true, Handle = window.Handle, Window = window, MatchCount = 1 };

    /// <summary>No single window, with the reason.</summary>
    public static WindowResolution Failed(string errorCode, string error, IReadOnlyList<WindowInfo>? matches = null) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        Error = error,
        Candidates = matches?.Take(MaxCandidates).ToList() ?? [],
        MatchCount = matches?.Count ?? 0
    };
}

/// <summary>
/// A window an ambiguous selector matched (roadmap M2.4): enough to name it exactly by its handle. IsAgentHost is true for
/// the agent's own terminal, editor or chat window, as find_windows marks it, and omitted for every other window, so an
/// agent picking a candidate can tell its own host apart.
/// </summary>
public sealed record WindowCandidateDto(long Handle, string Title, string ProcessName, int ProcessId, string ClassName, bool? IsAgentHost = null)
{
    /// <summary>The candidate for a window that was read.</summary>
    public static WindowCandidateDto From(WindowInfo window) =>
        new(window.Handle.ToInt64(), window.Title, window.ProcessName, window.ProcessId, window.ClassName, window.IsAgentHost ? true : null);
}
