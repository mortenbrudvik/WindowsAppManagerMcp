using System.Text.Json;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class LayoutService : ILayoutService
{
    private readonly IWindowService _windowService;
    private readonly IMonitorService _monitorService;
    private readonly IProcessService _processService;
    private readonly string _layoutsPath;
    private readonly Dictionary<string, LayoutPreset> _presetCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LayoutService(
        IWindowService windowService,
        IMonitorService monitorService,
        IProcessService processService,
        string? layoutsPath = null)
    {
        _windowService = windowService;
        _monitorService = monitorService;
        _processService = processService;
        _layoutsPath = layoutsPath ?? Path.Combine(AppContext.BaseDirectory, "layouts");

        Directory.CreateDirectory(_layoutsPath);
        LoadPresets();
    }

    private void LoadPresets()
    {
        _presetCache.Clear();

        if (!Directory.Exists(_layoutsPath))
            return;

        foreach (var file in Directory.EnumerateFiles(_layoutsPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var preset = JsonSerializer.Deserialize<LayoutPreset>(json, JsonOptions);
                if (preset != null)
                {
                    _presetCache[preset.Name] = preset;
                }
            }
            catch
            {
                // Skip invalid files
            }
        }
    }

    public IReadOnlyList<LayoutPreset> GetAllPresets()
    {
        LoadPresets();
        return _presetCache.Values.ToList();
    }

    public LayoutPreset? GetPreset(string name)
    {
        _presetCache.TryGetValue(name, out var preset);
        return preset;
    }

    public async Task<bool> SavePresetAsync(LayoutPreset preset, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = GetPresetFilePath(preset.Name);
            var json = JsonSerializer.Serialize(preset, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
            _presetCache[preset.Name] = preset;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> DeletePresetAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = GetPresetFilePath(name);
            if (File.Exists(filePath))
            {
                await Task.Run(() => File.Delete(filePath), cancellationToken);
            }
            _presetCache.Remove(name);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<LayoutPreset> CaptureCurrentLayoutAsync(
        string name,
        string? description = null,
        string[]? includeProcesses = null,
        string[]? excludeProcesses = null,
        CancellationToken cancellationToken = default)
    {
        var windows = _windowService.GetAllWindows(includeMinimized: false);
        var monitors = _monitorService.GetAllMonitors();
        var placements = new List<WindowPlacement>();

        foreach (var window in windows)
        {
            // Filter by process name
            if (includeProcesses?.Length > 0)
            {
                if (!includeProcesses.Any(p => window.ProcessName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            if (excludeProcesses?.Length > 0)
            {
                if (excludeProcesses.Any(p => window.ProcessName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            // Calculate relative position within monitor work area
            var monitor = monitors.ElementAtOrDefault(window.MonitorIndex) ?? monitors[0];
            var workArea = monitor.WorkArea;

            var relX = (double)(window.Bounds.X - workArea.X) / workArea.Width;
            var relY = (double)(window.Bounds.Y - workArea.Y) / workArea.Height;
            var relW = (double)window.Bounds.Width / workArea.Width;
            var relH = (double)window.Bounds.Height / workArea.Height;

            // Clamp to valid range
            relX = Math.Clamp(relX, 0, 1);
            relY = Math.Clamp(relY, 0, 1);
            relW = Math.Clamp(relW, 0.1, 1);
            relH = Math.Clamp(relH, 0.1, 1);

            var placement = new WindowPlacement(
                Matcher: new WindowMatcher(
                    TitleContains: null,
                    ProcessName: window.ProcessName,
                    ClassName: null
                ),
                MonitorIndex: window.MonitorIndex,
                Position: new RelativePosition(relX, relY, relW, relH),
                LaunchCommand: null,
                Focus: false,
                Order: placements.Count
            );

            placements.Add(placement);
        }

        var preset = new LayoutPreset(
            Name: name,
            Description: description,
            Placements: placements
        )
        {
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await SavePresetAsync(preset, cancellationToken);
        return preset;
    }

    public async Task<LayoutApplyResult> ApplyPresetAsync(
        string name,
        string matchBy = "process_and_title",
        bool launchMissing = false,
        CancellationToken cancellationToken = default)
    {
        var preset = GetPreset(name);
        if (preset == null)
        {
            return new LayoutApplyResult(
                Success: false,
                PresetName: name,
                Errors: ["Layout preset not found"]
            );
        }

        var monitors = _monitorService.GetAllMonitors();
        var errors = new List<string>();
        var arranged = 0;
        var notFound = 0;
        var launched = 0;

        // Sort placements by order
        var orderedPlacements = preset.Placements.OrderBy(p => p.Order).ToList();

        foreach (var placement in orderedPlacements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Find matching window
            var windows = FindMatchingWindows(placement.Matcher, matchBy);

            if (windows.Count == 0)
            {
                if (launchMissing && !string.IsNullOrEmpty(placement.LaunchCommand))
                {
                    // Try to launch the application
                    var launchResult = _processService.LaunchApplication(
                        placement.LaunchCommand,
                        waitForWindow: true,
                        waitTimeoutMs: 5000);

                    if (launchResult.Success && launchResult.WindowHandle.HasValue)
                    {
                        windows = [_windowService.GetWindowInfo(launchResult.WindowHandle.Value)!];
                        launched++;
                    }
                    else
                    {
                        errors.Add($"Failed to launch: {placement.LaunchCommand}");
                        notFound++;
                        continue;
                    }
                }
                else
                {
                    notFound++;
                    continue;
                }
            }

            // Apply placement to the first matching window
            var window = windows[0];
            var targetMonitor = monitors.ElementAtOrDefault(placement.MonitorIndex) ?? monitors[0];
            var workArea = targetMonitor.WorkArea;

            var x = (int)(workArea.X + placement.Position.X * workArea.Width);
            var y = (int)(workArea.Y + placement.Position.Y * workArea.Height);
            var w = (int)(placement.Position.Width * workArea.Width);
            var h = (int)(placement.Position.Height * workArea.Height);

            if (_windowService.SetWindowBounds(window.Handle, x, y, w, h))
            {
                arranged++;

                if (placement.Focus)
                {
                    _windowService.FocusWindow(window.Handle);
                }
            }
            else
            {
                errors.Add($"Failed to arrange window: {window.Title}");
            }

            // Small delay between window operations
            await Task.Delay(50, cancellationToken);
        }

        return new LayoutApplyResult(
            Success: errors.Count == 0,
            PresetName: name,
            WindowsArranged: arranged,
            WindowsNotFound: notFound,
            WindowsLaunched: launched,
            Errors: errors.Count > 0 ? errors : null
        );
    }

    private IReadOnlyList<WindowInfo> FindMatchingWindows(WindowMatcher matcher, string matchBy)
    {
        var windows = _windowService.GetAllWindows(includeMinimized: true);
        var results = new List<WindowInfo>();

        foreach (var window in windows)
        {
            var matches = matchBy.ToLowerInvariant() switch
            {
                "process_only" => MatchesProcessName(window, matcher),
                "title_only" => MatchesTitle(window, matcher),
                _ => MatchesProcessName(window, matcher) && MatchesTitle(window, matcher)
            };

            if (matches)
            {
                results.Add(window);
            }
        }

        return results;
    }

    private static bool MatchesProcessName(WindowInfo window, WindowMatcher matcher)
    {
        if (string.IsNullOrEmpty(matcher.ProcessName))
            return true;

        return window.ProcessName.Contains(matcher.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesTitle(WindowInfo window, WindowMatcher matcher)
    {
        if (string.IsNullOrEmpty(matcher.TitleContains))
            return true;

        return window.Title.Contains(matcher.TitleContains, StringComparison.OrdinalIgnoreCase);
    }

    private string GetPresetFilePath(string name)
    {
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_layoutsPath, $"{safeName}.json");
    }
}
