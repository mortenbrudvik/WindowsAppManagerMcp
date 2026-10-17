namespace WindowsAppManagerMcp.Services;

/// <summary>What the results say when takeover detection (roadmap M7.4) saw the user's input during an operation.</summary>
public static class TakeoverMessages
{
    /// <summary>The UserInterrupted error: <paramref name="done"/> says how far the operation got.</summary>
    public static string Interrupted(string done) =>
        $"{done} The user used the keyboard or mouse while it ran, so takeover detection (WAM_TAKEOVER_DETECTION=stop) stopped " +
        "it there and released what it held. Do not try again at once: the user is using the computer. Ask the user before " +
        "you go on.";

    /// <summary>The warning of mode warn: <paramref name="what"/> names the operation, such as "the text was typed".</summary>
    public static string Warning(string what) =>
        $"The user used the keyboard or mouse while {what} (takeover detection, WAM_TAKEOVER_DETECTION=warn): their input may be " +
        "mixed into it, or the focus or the pointer may have moved. Check the result before you go on.";

    /// <summary><paramref name="first"/> and <paramref name="second"/> as one warning; null when both are.</summary>
    public static string? Combine(string? first, string? second) =>
        first == null ? second : second == null ? first : $"{first} {second}";
}
