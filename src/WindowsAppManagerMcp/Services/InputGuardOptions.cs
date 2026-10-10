namespace WindowsAppManagerMcp.Services;

/// <summary>
/// A key chord that send_keys refuses without an explicit target window (DestructiveChordNeedsTarget): it
/// can close, quit or lock whatever window is in front, which is often the agent's own terminal.
/// </summary>
/// <param name="Spec">The chord as configured, in send_keys syntax (for example <c>%{F4}</c>).</param>
/// <param name="Chord">The key and modifiers it stands for. The same key with more modifiers matches too.</param>
public sealed record DestructiveChord(string Spec, KeyChord Chord);

/// <summary>
/// Settings of the input guard that the user can change in the server's environment.
/// </summary>
/// <param name="DestructiveChords">The chords that need an explicit target window.</param>
/// <param name="InvalidChordEntries">
/// Entries of <see cref="DestructiveChordsVariable"/> that are not a single chord in send_keys syntax;
/// they are ignored and logged.
/// </param>
/// <param name="DestructiveChordSource">"default", or <see cref="DestructiveChordsVariable"/>.</param>
public sealed record InputGuardOptions(
    IReadOnlyList<DestructiveChord> DestructiveChords,
    IReadOnlyList<string> InvalidChordEntries,
    string DestructiveChordSource)
{
    /// <summary>
    /// Environment variable with the destructive chords, separated by spaces (commas and semicolons are
    /// keys). It replaces the default list; the entry "default" puts the default list back in, and "none"
    /// alone turns the rule off. <c>#</c> is the Windows-key modifier here. An entry that is not one chord
    /// is ignored and logged, and the default list then stays in force as well.
    /// </summary>
    public const string DestructiveChordsVariable = "WAM_DESTRUCTIVE_CHORDS";

    /// <summary>The value of <see cref="DestructiveChordSource"/> when the default list applies.</summary>
    public const string DefaultSource = "default";

    /// <summary>
    /// Alt+F4 (close the window), Ctrl+W (close the tab or window), Ctrl+Q (quit), Ctrl+F4 (close the
    /// document or tab), Ctrl+Shift+Esc (Task Manager) and Win+L (lock the session). send_keys has no
    /// Windows-key modifier today, so Win+L is listed for when it has one.
    /// </summary>
    public static IReadOnlyList<string> DefaultDestructiveChords { get; } = ["%{F4}", "^w", "^q", "^{F4}", "^+{ESC}", "#l"];

    /// <summary>
    /// Chords that hand the keyboard to another window: Alt+Tab, Alt+Esc (switch windows), Ctrl+Esc and the
    /// Windows key (the Start menu), and Alt+Space (the window menu, whose items minimize or close the
    /// window). Together with the destructive chords, which close the window, they must be the last key of
    /// a send_keys call: keys after them would reach a window the guard never checked. Fixed, not
    /// configurable.
    /// </summary>
    public static IReadOnlyList<string> FocusChangingChords { get; } = ["%{TAB}", "%{ESC}", "^{ESC}", "%{SPACE}", "{LWIN}", "{RWIN}"];

    private static readonly IReadOnlyList<(string Spec, KeyChord Chord)> FocusChanging = FocusChangingChords
        .Select(spec => (spec, SendKeysParser.KeyPresses(SendKeysParser.Parse(spec)).Single()))
        .ToList();

    /// <summary>The options when nothing is configured.</summary>
    public static InputGuardOptions Default { get; } = FromEnvironment(_ => null);

    /// <summary>Reads the options from the server's environment.</summary>
    public static InputGuardOptions FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>Reads the options from <paramref name="readVariable"/>; an unset or empty variable keeps the defaults.</summary>
    public static InputGuardOptions FromEnvironment(Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        var entries = (readVariable(DestructiveChordsVariable) ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
        {
            return Build(DefaultDestructiveChords, DefaultSource);
        }

        if (entries.Length == 1 && string.Equals(entries[0], "none", StringComparison.OrdinalIgnoreCase))
        {
            return new InputGuardOptions([], [], DestructiveChordsVariable);
        }

        var specs = entries.SelectMany(entry =>
            string.Equals(entry, "default", StringComparison.OrdinalIgnoreCase) ? DefaultDestructiveChords : [entry]).ToList();
        var options = Build(specs, DestructiveChordsVariable);

        // Fails closed: a mistyped entry (a comma-separated list, say) keeps the default chords in force too,
        // instead of silently leaving Alt+F4 and the others unprotected
        return options.InvalidChordEntries.Count == 0
            ? options
            : Build(specs.Concat(DefaultDestructiveChords), DestructiveChordsVariable);
    }

    /// <summary>
    /// The first destructive chord pressed in <paramref name="events"/> (parsed send_keys input), or null.
    /// </summary>
    public DestructiveChord? FindDestructiveChord(IReadOnlyList<Models.InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var pressed = SendKeysParser.Chords(events);
        foreach (var chord in pressed)
        {
            if (DestructiveChords.FirstOrDefault(d => d.Chord.IsPartOf(chord)) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// The first chord in <paramref name="events"/> (parsed send_keys input) that moves the keyboard to
    /// another window (<see cref="FocusChangingChords"/>) or closes the window (a destructive chord) and is
    /// followed by more key presses, as its spec; null when there is none. Only the last key of a call may be
    /// such a chord.
    /// </summary>
    public string? FindKeysAfterFocusChange(IReadOnlyList<Models.InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var pressed = SendKeysParser.KeyPresses(events);
        for (var i = 0; i < pressed.Count - 1; i++)
        {
            if (FocusChanging.FirstOrDefault(f => f.Chord.IsPartOf(pressed[i])) is { Spec: { } focus })
            {
                return focus;
            }

            if (DestructiveChords.FirstOrDefault(d => d.Chord.IsPartOf(pressed[i])) is { } destructive)
            {
                return destructive.Spec;
            }
        }

        return null;
    }

    private static InputGuardOptions Build(IEnumerable<string> specs, string source)
    {
        var chords = new List<DestructiveChord>();
        var invalid = new List<string>();
        foreach (var spec in specs.Distinct(StringComparer.Ordinal))
        {
            if (TryParseChord(spec) is { } chord)
            {
                chords.Add(new DestructiveChord(spec, chord));
            }
            else
            {
                invalid.Add(spec);
            }
        }

        return new InputGuardOptions(chords, invalid, source);
    }

    /// <summary>The chord <paramref name="spec"/> stands for, or null when it is not exactly one key press.</summary>
    private static KeyChord? TryParseChord(string spec)
    {
        try
        {
            var chords = SendKeysParser.Chords(SendKeysParser.Parse(spec, windowsModifier: true));
            return chords.Count == 1 ? chords[0] : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
