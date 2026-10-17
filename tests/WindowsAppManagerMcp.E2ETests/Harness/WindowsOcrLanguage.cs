using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// An installed Windows OCR language, which the OCR scenarios need (roadmap M6.3): gated with
/// <c>Requires = typeof(WindowsOcrLanguage)</c>, they are skipped with this reason on a machine without one instead of failing
/// with OcrUnavailable. Asks Windows.Media.Ocr for its recognizer languages; it reads no screen and installs nothing (the
/// server never installs a language either).
/// </summary>
public sealed class WindowsOcrLanguage : IDesktopTestRequirement
{
    /// <inheritdoc />
    public string? MissingReason
    {
        get
        {
            try
            {
                using var engine = new OcrEngineWrapper();
                return engine.GetCapabilities().Languages.Count > 0
                    ? null
                    : "No Windows OCR language is installed. Add one in Settings > Time & language > Language & region (a " +
                      "language with its optical character recognition feature) to run this scenario.";
            }
            catch (OcrUnavailableException ex)
            {
                return ex.Message;
            }
        }
    }
}

/// <summary>
/// A monitor at another scale (<see cref="MonitorAtAnotherScale"/>) and an installed Windows OCR language
/// (<see cref="WindowsOcrLanguage"/>): an OCR scenario on that monitor needs both, and <c>Requires</c> takes one type.
/// </summary>
public sealed class MonitorAtAnotherScaleAndOcrLanguage : IDesktopTestRequirement
{
    /// <inheritdoc />
    public string? MissingReason => new MonitorAtAnotherScale().MissingReason ?? new WindowsOcrLanguage().MissingReason;
}
