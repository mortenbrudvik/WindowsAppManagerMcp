using System.Collections.Concurrent;
using System.Runtime.Versioning;
using WindowsAppManagerMcp.Services.Interfaces;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Windows OCR through the slim CsWinRT projection of Windows.Media.Ocr (roadmap M6.3, owner decision D6; see the csproj).
/// The only server code that uses the projection (NativeCallGuardTests' WinRtProjection tests). Nothing is loaded until the first call, so a
/// server without the ocr toolset never touches WinRT. Engines are created once per language and kept; one recognition runs
/// at a time, which bounds the memory a burst of calls can take. Every WinRT failure becomes an
/// <see cref="OcrUnavailableException"/>, so a system without Windows OCR (or with a broken one) fails the call, not the server.
/// </summary>
public sealed class OcrEngineWrapper : IOcrEngineWrapper, IDisposable
{
    // OcrEngine instances, typed object: a field cannot be guarded by the Windows 10 check (CA1416)
    private readonly ConcurrentDictionary<string, object> _engines = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _recognition = new(1, 1);

    // The last capabilities read: the profile language is asked for again only when the installed languages changed, since
    // asking creates an engine
    private OcrCapabilities? _capabilities;

    /// <inheritdoc />
    public OcrCapabilities GetCapabilities()
    {
        if (!IsWindows10)
        {
            throw OlderThanWindows10();
        }

        return ReadCapabilities();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private OcrCapabilities ReadCapabilities()
    {
        try
        {
            var languages = OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToArray();
            if (Volatile.Read(ref _capabilities) is { } known && known.Languages.SequenceEqual(languages, StringComparer.OrdinalIgnoreCase))
            {
                return known;
            }

            var profile = OcrEngine.TryCreateFromUserProfileLanguages();
            var profileTag = profile?.RecognizerLanguage.LanguageTag;
            if (profile != null && profileTag != null)
            {
                _engines.TryAdd(profileTag, profile);
            }

            var capabilities = new OcrCapabilities(languages, profileTag, (int)OcrEngine.MaxImageDimension);
            Volatile.Write(ref _capabilities, capabilities);
            return capabilities;
        }
        catch (Exception ex) when (IsWinRtFailure(ex))
        {
            throw new OcrUnavailableException($"Windows OCR (Windows.Media.Ocr) cannot be used on this system: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<OcrPage> RecognizeAsync(byte[] bgra, int width, int height, string languageTag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentException.ThrowIfNullOrEmpty(languageTag);
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
        {
            throw new ArgumentException($"A {width}x{height} BGRA image needs {(long)width * height * 4} bytes, not {bgra.Length}.", nameof(bgra));
        }

        if (!IsWindows10)
        {
            throw OlderThanWindows10();
        }

        return await RecognizeOnWindows10Async(bgra, width, height, languageTag, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private async Task<OcrPage> RecognizeOnWindows10Async(byte[] bgra, int width, int height, string languageTag, CancellationToken cancellationToken)
    {
        await _recognition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var engine = Engine(languageTag);
            OcrResult result;
            try
            {
                // CryptographicBuffer copies the bytes into a WinRT buffer: AsBuffer() lives in the full SDK projection.
                // Ignore: the 4th byte of a GDI capture is not alpha
                var buffer = CryptographicBuffer.CreateFromByteArray(bgra);
                using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
                var operation = engine.RecognizeAsync(bitmap);
                try
                {
                    result = await operation.AsTask(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // AsTask cancels the operation but returns before it has stopped: wait for that, so the bitmap and the
                    // one-recognition-at-a-time lock outlive it
                    await WhileRunningAsync(operation).ConfigureAwait(false);
                    throw;
                }
            }
            catch (Exception ex) when (IsWinRtFailure(ex))
            {
                throw new OcrUnavailableException($"Windows OCR could not read the image: {ex.Message}", ex);
            }

            return new OcrPage(
                result.TextAngle,
                result.Lines.Select(line => new OcrPageLine(
                    line.Text,
                    line.Words.Select(word =>
                    {
                        var box = word.BoundingRect;
                        return new OcrPageWord(word.Text, box.X, box.Y, box.Width, box.Height);
                    }).ToArray())).ToArray());
        }
        finally
        {
            _recognition.Release();
        }
    }

    public void Dispose() => _recognition.Dispose();

    /// <summary>The longest a cancelled recognition is waited for before the call returns anyway.</summary>
    private static readonly TimeSpan CancelledRecognitionWait = TimeSpan.FromSeconds(5);

    /// <summary>Waits until <paramref name="operation"/> has stopped running, for at most <see cref="CancelledRecognitionWait"/>.</summary>
    [SupportedOSPlatform("windows10.0.10240")]
    private static async Task WhileRunningAsync(Windows.Foundation.IAsyncInfo operation)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (operation.Status == Windows.Foundation.AsyncStatus.Started &&
               System.Diagnostics.Stopwatch.GetElapsedTime(started) < CancelledRecognitionWait)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>Windows.Media.Ocr, SoftwareBitmap and CryptographicBuffer came with Windows 10 (build 10240).</summary>
    [SupportedOSPlatformGuard("windows10.0.10240")]
    private static bool IsWindows10 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240);

    private static OcrUnavailableException OlderThanWindows10() =>
        new($"Windows OCR needs Windows 10 or later; this is {Environment.OSVersion.VersionString}.");

    [SupportedOSPlatform("windows10.0.10240")]
    private OcrEngine Engine(string languageTag)
    {
        if (_engines.TryGetValue(languageTag, out var cached))
        {
            return (OcrEngine)cached;
        }

        OcrEngine? engine;
        try
        {
            engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
        }
        // ArgumentException: Windows refuses the tag as a language
        catch (Exception ex) when (IsWinRtFailure(ex) || ex is ArgumentException)
        {
            throw new OcrUnavailableException($"Windows OCR has no engine for '{languageTag}': {ex.Message}", ex);
        }

        return engine == null
            ? throw new OcrUnavailableException($"Windows OCR has no engine for '{languageTag}'.")
            : (OcrEngine)_engines.GetOrAdd(languageTag, engine);
    }

    /// <summary>
    /// What a missing or broken Windows OCR throws: a COM error from the API (also access denied), or a projection that cannot
    /// load its types or entry points. Argument, cast and other errors are the server's own bugs (it checks the image and
    /// asks only for installed languages), so they pass through as such instead of reading as "OCR is unavailable"; so does
    /// cancellation.
    /// </summary>
    private static bool IsWinRtFailure(Exception ex) =>
        ex is System.Runtime.InteropServices.COMException or TypeInitializationException or TypeLoadException or
            DllNotFoundException or EntryPointNotFoundException or UnauthorizedAccessException or
            System.Runtime.InteropServices.SEHException;
}
