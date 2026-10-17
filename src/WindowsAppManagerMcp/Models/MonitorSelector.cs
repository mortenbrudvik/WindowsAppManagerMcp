using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A `monitor` argument (roadmap M6.7, owner decision D13), as the call gave it: an index (a JSON number, or a string of
/// digits), or a name: 'primary', 'left' or 'right' (the leftmost or rightmost monitor), 'current' (the foreground window's),
/// 'all' (every monitor, where a capture takes it), a monitor id ("m-" and 8 hex digits) or a device name (\\.\DISPLAY2).
/// The input schema offers an integer or a string (ToolJson writes <c>"type": ["integer", "string"]</c> for it); anything
/// else fails to bind. MonitorSelection resolves it against the monitors there are.
/// </summary>
[JsonConverter(typeof(MonitorSelectorConverter))]
public sealed record MonitorSelector
{
    private MonitorSelector(int? index, string? name)
    {
        Index = index;
        Name = name;
    }

    /// <summary>The index the call gave, or null for a name.</summary>
    public int? Index { get; }

    /// <summary>The name the call gave, trimmed, in the case it was given; null for an index.</summary>
    public string? Name { get; }

    public static MonitorSelector OfIndex(int index) => new(index, null);

    /// <summary>The selector a string names: an index when it is an integer, otherwise a name.</summary>
    /// <exception cref="ArgumentException">The string is empty or white space.</exception>
    public static MonitorSelector Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("A monitor name cannot be empty.", nameof(text));

        return int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index)
            ? OfIndex(index)
            : new MonitorSelector(null, trimmed);
    }

    /// <summary>The selector as the call gave it: the index, or the name.</summary>
    public override string ToString() => Index?.ToString(CultureInfo.InvariantCulture) ?? Name!;
}

/// <summary>The descriptions of the `monitor` parameters (roadmap M6.7).</summary>
public static class MonitorSelectorDescriptions
{
    /// <summary>The description of every `monitor` parameter.</summary>
    public const string Monitor =
        "A monitor: index, 'primary', 'left', 'right', 'current' (the foreground window's, maybe your own terminal's), or an id " +
        "or name from get_monitors.";

    /// <summary>The description of a `monitor` parameter that also takes every monitor in one image.</summary>
    public const string MonitorOrAll = Monitor + " 'all' (or -1): every monitor in one image.";
}

/// <summary>Reads a <see cref="MonitorSelector"/> from a JSON integer or string, and writes it back as given.</summary>
public sealed class MonitorSelectorConverter : JsonConverter<MonitorSelector>
{
    private const string Expected =
        "monitor takes an index (0-based) or a name: 'primary', 'left', 'right', 'current', or an id or name from get_monitors.";

    public override MonitorSelector Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number when reader.TryGetInt32(out var index):
                return MonitorSelector.OfIndex(index);

            // 1.0 and 1e0 are the index 1 too: some clients write every number as a double
            case JsonTokenType.Number when reader.TryGetDouble(out var number) && number == Math.Floor(number) &&
                                           number is >= int.MinValue and <= int.MaxValue:
                return MonitorSelector.OfIndex((int)number);

            case JsonTokenType.String when reader.GetString() is { } text && !string.IsNullOrWhiteSpace(text):
                return MonitorSelector.Of(text);

            default:
                throw new JsonException(Expected);
        }
    }

    public override void Write(Utf8JsonWriter writer, MonitorSelector value, JsonSerializerOptions options)
    {
        if (value.Index is { } index)
        {
            writer.WriteNumberValue(index);
            return;
        }

        writer.WriteStringValue(value.Name);
    }
}

/// <summary>
/// What a <see cref="MonitorSelector"/> named: the index (passed on as given for an index, which each tool checks against the
/// monitors itself; <see cref="Services.MonitorSelection.AllMonitors"/> for 'all') and the monitor (null for 'all' and for an
/// index that is no monitor), or why it names none (MonitorNotFound, AmbiguousMonitor).
/// </summary>
public sealed record MonitorResolution(int Index, MonitorInfo? Monitor, string? ErrorCode = null, string? Error = null)
{
    public bool Succeeded => ErrorCode == null;

    public static MonitorResolution Found(int index, MonitorInfo? monitor) => new(index, monitor);

    public static MonitorResolution Failed(string errorCode, string error) => new(-1, null, errorCode, error);
}
