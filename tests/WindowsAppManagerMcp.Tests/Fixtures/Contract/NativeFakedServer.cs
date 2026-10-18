using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// The server's real services, registered as Program.cs registers them (<see cref="ServiceRegistration.AddWindowsAppManagerServices"/>),
/// with every native seam replaced by a fake: every interface of the server named INative* or *Wrapper, every server interface a
/// type of the Native namespace implements (IUiaTree, IScreenCaptureWrapper, ITcpPortOwnerResolver, ...) and the CDP transport.
/// NativeCallGuardTests keeps every action of the server behind those seams, so nothing here can reach the desktop, a process,
/// the clipboard or a browser: the fakes answer from a small desktop of their own (<see cref="DeniedDesktop"/>). No hosted service
/// runs (no pause hotkey, no host recording at start). Before any call, the container's services are walked and the harness
/// refuses to start when one of them holds a real native wrapper.
/// </summary>
public sealed class NativeFakedServer : IDisposable
{
    private static readonly Assembly ServerAssembly = typeof(WindowService).Assembly;
    private const string NativeNamespace = "WindowsAppManagerMcp.Native";

    /// <summary>The seams: the server's native wrapper interfaces and the CDP transport.</summary>
    public static IReadOnlySet<Type> Seams { get; } = FindSeams();

    private readonly Dictionary<Type, object> _fakes = new();
    private readonly List<ServiceDescriptor> _registrations;
    private readonly ServiceProvider _provider;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WamNativeFaked", Guid.NewGuid().ToString("N"));

    /// <param name="policy">The user's target policy.</param>
    public NativeFakedServer(TargetPolicySettings policy)
    {
        var services = new ServiceCollection();
        services.AddWindowsAppManagerServices(
            () => LayoutStorageOptions.ForPath(Path.Combine(_folder, "layouts")),
            () => AuditLogOptions.ForFolder(Path.Combine(_folder, "audit")));
        // MonitorService creates its own monitor wrapper unless the container has one
        services.TryAddSingleton<INativeMonitorWrapper, NativeMonitorWrapper>();

        Desktop = new DeniedDesktop(this);
        for (var i = 0; i < services.Count; i++)
        {
            if (!services[i].IsKeyedService && Seams.Contains(services[i].ServiceType))
            {
                services[i] = ServiceDescriptor.Singleton(services[i].ServiceType, Fake(services[i].ServiceType));
            }
        }

        // The settings the user would set in the server's environment, fixed here so the machine's own do not count
        services.Replace(ServiceDescriptor.Singleton<ITargetPolicy>(new TargetPolicy(policy, Mock<INativeWindowWrapper>().Object)));
        services.Replace(ServiceDescriptor.Singleton(InputGuardOptions.Default));
        services.Replace(ServiceDescriptor.Singleton(TakeoverOptions.Default));
        services.Replace(ServiceDescriptor.Singleton(new ClipboardOptions(false)));

        Desktop.SetUp();
        _registrations = services.ToList();
        _provider = services.BuildServiceProvider();
        ThrowIfARealNativeWrapperIsReachable();
    }

    /// <summary>The fakes' desktop: the windows, processes and monitors they report.</summary>
    public DeniedDesktop Desktop { get; }

    /// <summary>The fake behind <typeparamref name="T"/>: a Moq mock unless <see cref="Fake"/> uses one of the test fakes.</summary>
    public Mock<T> Mock<T>() where T : class => (Mock<T>)_fakes[typeof(T)];

    /// <summary>The test fake behind <typeparamref name="TService"/>, such as <see cref="FakeInputWrapper"/>.</summary>
    public TFake FakeOf<TService, TFake>() where TFake : class => (TFake)_fakes[typeof(TService)];

    /// <summary>Every fake's recorded calls that change something: a window, input, a process, the clipboard, a page, an element.</summary>
    public IReadOnlyList<string> Actions()
    {
        var actions = new List<string>();
        foreach (var (service, fake) in _fakes)
        {
            if (fake is Mock mock)
            {
                actions.AddRange(mock.Invocations.Where(i => IsAction(service, i.Method)).Select(i => $"{service.Name}.{i.Method.Name}"));
            }
        }

        actions.AddRange(Desktop.ProcessHandle.Invocations.Where(i => IsAction(typeof(IProcessHandle), i.Method))
            .Select(i => $"{nameof(IProcessHandle)}.{i.Method.Name}"));
        var input = FakeOf<INativeInputWrapper, FakeInputWrapper>();
        actions.AddRange(input.Batches.Select(b => $"SendInput ({b.Count} events)"));
        if (input.CursorMoves > 0)
        {
            actions.Add($"SetCursorPos ({input.CursorMoves})");
        }

        var clipboard = FakeOf<INativeClipboardWrapper, FakeClipboardWrapper>();
        if (clipboard.Writes > 0)
        {
            actions.Add($"clipboard writes ({clipboard.Writes})");
        }

        actions.AddRange(Desktop.Tree.Reads.Where(r => r.StartsWith("SetFocus:", StringComparison.Ordinal)));
        if (Desktop.Button.Invokes + Desktop.DialogButton.Invokes > 0)
        {
            actions.Add($"Invoke of a denied button ({Desktop.Button.Invokes + Desktop.DialogButton.Invokes})");
        }

        return actions;
    }

    /// <summary>
    /// Starts the in-process MCP host with the server's own tools and call filters
    /// (<see cref="ServiceRegistration.AddWindowsAppManagerTools"/>) over these services: the call goes through the real pipeline
    /// and the real services, and reaches only the fakes.
    /// </summary>
    public Task<InProcessMcpServer> StartAsync() =>
        InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            configureServices: host =>
            {
                foreach (var type in _registrations
                             .Where(d => !d.IsKeyedService && !d.ServiceType.IsGenericTypeDefinition && d.ServiceType != typeof(IHostedService))
                             .Select(d => d.ServiceType)
                             .Distinct())
                {
                    if (!host.Any(d => d.ServiceType == type))
                    {
                        host.AddSingleton(type, _ => _provider.GetRequiredService(type));
                    }
                }
            });

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- the fakes ----

    private object Fake(Type service)
    {
        if (!_fakes.TryGetValue(service, out var fake))
        {
            fake = service switch
            {
                _ when service == typeof(INativeInputWrapper) => new FakeInputWrapper(),
                _ when service == typeof(INativeClipboardWrapper) => new FakeClipboardWrapper(),
                _ when service == typeof(INativeWinEventWrapper) => new FakeWinEventWrapper(),
                _ when service == typeof(INativeHotkeyWrapper) => new FakeHotkeyWrapper(),
                _ when service == typeof(INativeLowLevelInputWrapper) => new FakeLowLevelInputWrapper(),
                _ when service == typeof(IOcrEngineWrapper) => new FakeOcrEngine(),
                _ when service == typeof(IUiaTree) => Desktop.Tree,
                _ => Activator.CreateInstance(typeof(Mock<>).MakeGenericType(service))!
            };
            _fakes[service] = fake;
        }

        return fake is Mock mock ? mock.Object : fake;
    }

    /// <summary>A member of a seam that changes something, not a read (as the E2E WrapperCatalog classifies them).</summary>
    private static bool IsAction(Type service, MethodInfo method) => method.Name switch
    {
        _ when service == typeof(INativeWindowWrapper) => method.Name is nameof(INativeWindowWrapper.SetWindowPos)
            or nameof(INativeWindowWrapper.SetWindowZOrder) or nameof(INativeWindowWrapper.ShowWindow)
            or nameof(INativeWindowWrapper.SetWindowPlacement) or nameof(INativeWindowWrapper.SetForegroundWindow)
            or nameof(INativeWindowWrapper.BringWindowToTop) or nameof(INativeWindowWrapper.PostMessage),
        _ when service == typeof(INativeProcessWrapper) => method.Name is nameof(INativeProcessWrapper.StartProcess),
        _ when service == typeof(IProcessHandle) => method.Name is nameof(IProcessHandle.Kill) or nameof(IProcessHandle.CloseMainWindow),
        _ when service == typeof(INativeThreadInputWrapper) => method.Name is nameof(INativeThreadInputWrapper.AttachThreadInput),
        _ when service == typeof(IUIAutomationWrapper) => true,
        _ when service == typeof(INativeAppsWrapper) => method.Name.StartsWith("Activate", StringComparison.Ordinal),
        // A browser's DevTools connection: anything sent is a command to a page
        _ when service == typeof(ICdpTransport) => true,
        _ => false
    };

    private static HashSet<Type> FindSeams()
    {
        var types = ServerAssembly.GetTypes();
        var seams = types
            .Where(t => t.IsInterface && t.Namespace?.StartsWith("WindowsAppManagerMcp", StringComparison.Ordinal) == true &&
                        (t.Name.StartsWith("INative", StringComparison.Ordinal) || t.Name.EndsWith("Wrapper", StringComparison.Ordinal)))
            .ToHashSet();
        foreach (var native in types.Where(t => t.Namespace == NativeNamespace && !t.IsInterface))
        {
            seams.UnionWith(native.GetInterfaces().Where(i => i.Assembly == ServerAssembly));
        }

        seams.Add(typeof(ICdpTransport));
        return seams;
    }

    /// <summary>
    /// Resolves every service and walks what each holds (fields, through the server's own types and their collections): none may
    /// be a real native wrapper, the real CDP transport or its socket factory. Fails closed: a service that cannot be resolved
    /// fails the harness too.
    /// </summary>
    private void ThrowIfARealNativeWrapperIsReachable()
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var found = new List<string>();
        foreach (var type in _registrations
                     .Where(d => !d.IsKeyedService && !d.ServiceType.IsGenericTypeDefinition && d.ServiceType != typeof(IHostedService))
                     .Select(d => d.ServiceType)
                     .Distinct())
        {
            Walk(_provider.GetRequiredService(type), type.Name, 0);
        }

        if (found.Count > 0)
        {
            throw new InvalidOperationException("The harness would hand real native code to the server: " + string.Join("; ", found));
        }

        void Walk(object? value, string path, int depth)
        {
            if (value == null || depth > 8 || value is string || value.GetType().IsPrimitive || !visited.Add(value))
            {
                return;
            }

            var type = value.GetType();
            if ((type.Namespace == NativeNamespace && type.GetInterfaces().Any(Seams.Contains)) ||
                type == typeof(CdpTransport) || type == typeof(ClientWebSocketFactory))
            {
                found.Add($"{path} is a {type.Name}");
                return;
            }

            if (value is IEnumerable items && type.Assembly != ServerAssembly && type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true)
            {
                var index = 0;
                foreach (var item in items)
                {
                    if (index++ > 50)
                    {
                        break;
                    }

                    Walk(item, $"{path}[]", depth + 1);
                }

                return;
            }

            if (type.Assembly != ServerAssembly && !IsDelegateWithServerTarget(value))
            {
                return;
            }

            if (value is Delegate function)
            {
                Walk(function.Target, $"{path}.Target", depth + 1);
                return;
            }

            for (var current = type; current != null && current.Assembly == ServerAssembly; current = current.BaseType)
            {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (!field.FieldType.IsValueType)
                    {
                        Walk(field.GetValue(value), $"{path}.{field.Name}", depth + 1);
                    }
                }
            }
        }

        static bool IsDelegateWithServerTarget(object value) =>
            value is Delegate { Target: { } target } && target.GetType().Assembly == ServerAssembly;
    }
}

/// <summary>
/// The desktop the faked seams report: a browser window of an app the user's policy denies (<see cref="DeniedWindow"/>, Chrome,
/// with the policy's deny list plus chrome), Notepad in front, one monitor, a cursor in Notepad. The denied window is a normal,
/// visible, responsive top-level window of its own process, so a tool that refuses it refuses it for the policy and not for being
/// minimized, hidden, hung or gone. UI Automation can read it (<see cref="Tree"/>): a refusal must come before the read.
/// </summary>
public sealed class DeniedDesktop
{
    public const long DeniedWindow = 0xD30010;
    public const int DeniedProcessId = 4711;
    public const string DeniedProcessName = "chrome";
    public const long AllowedWindow = 0xD30020;

    /// <summary>A dialog of the denied app (a #32770 Save As box owned by its window), for the tools that act on a dialog.</summary>
    public const long DeniedDialog = 0xD30030;
    public const int AllowedProcessId = 2001;

    private readonly NativeFakedServer _server;

    internal DeniedDesktop(NativeFakedServer server) => _server = server;

    /// <summary>The policy of this desktop: the default deny list (password managers, security prompts) and chrome.</summary>
    public static TargetPolicySettings Policy { get; } =
        TargetPolicySettings.Default with { DeniedProcesses = [.. TargetPolicySettings.DefaultDeniedProcesses, DeniedProcessName] };

    /// <summary>The UI Automation tree: the denied window with a button, which no tool may read or press.</summary>
    public FakeUiaTree Tree { get; } = new();

    /// <summary>The denied window's button's Invoke pattern.</summary>
    public FakeInvokePattern Button { get; } = new();

    /// <summary>The denied dialog's Save button's Invoke pattern.</summary>
    public FakeInvokePattern DialogButton { get; } = new();

    /// <summary>The windows UI Automation was asked for (FromHandle), in order.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<nint> WindowsReadThroughUiAutomation { get; } = new();

    internal void SetUp()
    {
        var windows = _server.Mock<INativeWindowWrapper>();
        Window(windows, DeniedWindow, DeniedProcessId, DeniedProcessName, "Online banking - Google Chrome", "Chrome_WidgetWin_1", (100, 100, 1300, 900));
        Window(windows, AllowedWindow, AllowedProcessId, "notepad", "notes.txt - Notepad", "Notepad", (1400, 100, 2200, 900));
        Window(windows, DeniedDialog, DeniedProcessId, DeniedProcessName, "Save As", "#32770", (300, 200, 1100, 700));
        windows.Setup(w => w.GetWindow((nint)DeniedDialog, 4u /* GW_OWNER */)).Returns((nint)DeniedWindow);
        windows.Setup(w => w.GetWindowLongPtr((nint)DeniedDialog, -16)).Returns(new nint(unchecked((int)0x96C80000))); // WS_POPUP | WS_VISIBLE | WS_CAPTION | WS_SYSMENU
        windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>())).Callback((Func<nint, bool> callback) =>
        {
            _ = callback((nint)AllowedWindow) && callback((nint)DeniedDialog) && callback((nint)DeniedWindow);
        });
        windows.Setup(w => w.GetForegroundWindow()).Returns((nint)AllowedWindow);
        windows.Setup(w => w.GetCurrentProcessId()).Returns((uint)Environment.ProcessId);
        windows.Setup(w => w.GetShellWindow()).Returns((nint)0xD30001);

        var processes = _server.Mock<INativeProcessWrapper>();
        processes.Setup(p => p.GetProcessImagePath(DeniedProcessId)).Returns(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
        processes.Setup(p => p.GetProcessImagePath(AllowedProcessId)).Returns(@"C:\Windows\System32\notepad.exe");
        processes.Setup(p => p.GetProcessStartTime(It.IsAny<int>())).Returns(new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc));
        // A debug browser with a temporary profile of the server's own (never created), so close_debug_browser gets to its policy check
        var profile = DebugBrowserProfiles.FromEnvironment().NewTemporaryPath();
        processes.Setup(p => p.GetProcessCommandLine(DeniedProcessId))
            .Returns($"\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --remote-debugging-port=9222 --user-data-dir=\"{profile}\"");
        processes.Setup(p => p.GetProcessList()).Returns(new Dictionary<int, (int ParentProcessId, string ImageName)>
        {
            [DeniedProcessId] = (1, "chrome.exe"),
            [AllowedProcessId] = (1, "notepad.exe")
        });
        processes.Setup(p => p.GetAncestorProcesses()).Returns([]);
        processes.Setup(p => p.GetAncestorsOf(It.IsAny<int>())).Returns([]);
        processes.Setup(p => p.GetDescendantsOf(It.IsAny<int>())).Returns([]);
        var handle = new Mock<IProcessHandle>();
        handle.Setup(h => h.Id).Returns(DeniedProcessId);
        handle.Setup(h => h.ProcessName).Returns(DeniedProcessName);
        processes.Setup(p => p.OpenProcess(DeniedProcessId)).Returns(handle.Object);
        ProcessHandle = handle;
        processes.Setup(p => p.ListRunningProcesses()).Returns(
        [
            new RunningProcess(DeniedProcessId, DeniedProcessName, 300_000_000, 1),
            new RunningProcess(AllowedProcessId, "notepad", 20_000_000, 1)
        ]);

        var monitor = new MonitorInfo(0, @"\\.\DISPLAY1", true, new MonitorRect(0, 0, 2560, 1440), new MonitorRect(0, 0, 2560, 1392), 1.0);
        _server.Mock<INativeMonitorWrapper>().Setup(m => m.EnumerateMonitors()).Returns([((nint)0x10001, monitor)]);
        _server.Mock<INativeMonitorWrapper>().Setup(m => m.MonitorFromWindow(It.IsAny<nint>())).Returns((nint)0x10001);
        _server.Mock<INativeMonitorWrapper>().Setup(m => m.MonitorFromPoint(It.IsAny<int>(), It.IsAny<int>())).Returns((nint)0x10001);
        _server.Mock<IScreenCaptureWrapper>().Setup(s => s.GetVirtualScreenBounds()).Returns((0, 0, 2560, 1440));
        _server.Mock<INativeSystemInfoWrapper>().Setup(s => s.GetVirtualScreen()).Returns(new VirtualScreenBounds(0, 0, 2560, 1440));

        // Pointer and hit tests: the point over each window is that window
        _server.FakeOf<INativeInputWrapper, FakeInputWrapper>().VirtualScreen = new VirtualScreenBounds(0, 0, 2560, 1440);
        _server.FakeOf<INativeInputWrapper, FakeInputWrapper>().MovePointer(1800, 500);
        var hitTest = _server.Mock<INativeHitTestWrapper>();
        hitTest.Setup(h => h.WindowFromPoint(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int x, int _) => x < 1350 ? (nint)DeniedWindow : (nint)AllowedWindow);
        hitTest.Setup(h => h.GetRootWindow(It.IsAny<nint>())).Returns((nint w) => w);
        hitTest.Setup(h => h.GetClientAreaOnScreen((nint)DeniedWindow)).Returns(new WindowRect(108, 131, 1184, 761));
        var threads = _server.Mock<INativeThreadInputWrapper>();
        threads.Setup(t => t.GetCurrentThreadId()).Returns(1);
        threads.Setup(t => t.GetWindowThreadId(It.IsAny<nint>())).Returns((nint w) => (uint)(w & 0xFFFF) + 10);
        threads.Setup(t => t.GetGuiThreadInfo(It.IsAny<uint>())).Returns(new GuiThreadInfo(0, 0, 0, 0, 0));

        // The security probes: the server and both apps at medium integrity, the input desktop the user's
        var security = _server.Mock<INativeSecurityWrapper>();
        security.Setup(s => s.QueryProcessToken(It.IsAny<int>()))
            .Returns(new ProcessTokenQuery(TokenQueryStatus.Succeeded, new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc), ProcessIntegrity.MediumRid, false));
        security.Setup(s => s.QueryInputDesktop()).Returns(new InputDesktopQuery(true, "Default", 0));
        security.Setup(s => s.GetThreadDesktopName()).Returns("Default");
        security.Setup(s => s.QuerySessionState()).Returns(new SessionStateQuery(1, 0, 1)); // active, unlocked (WTS_SESSIONSTATE_UNLOCK)

        Tree.FromHandleFailure = window =>
        {
            WindowsReadThroughUiAutomation.Enqueue(window);
            return null;
        };
        Tree.WithWindow((nint)DeniedWindow, new FakeUiaNode("Window", "Online banking - Google Chrome")
        {
            ProcessId = DeniedProcessId, NativeWindowHandle = (nint)DeniedWindow, ClassName = "Chrome_WidgetWin_1"
        }.With(new FakeUiaNode("Button", "Transfer money") { ProcessId = DeniedProcessId, ActionPatterns = ["Invoke"] }.WithPattern<IUiaInvokePattern>(Button)));
        Tree.WithWindow((nint)DeniedDialog, new FakeUiaNode("Window", "Save As")
        {
            ProcessId = DeniedProcessId, NativeWindowHandle = (nint)DeniedDialog, ClassName = "#32770"
        }.With(new FakeUiaNode("Button", "Save") { ProcessId = DeniedProcessId, ActionPatterns = ["Invoke"] }.WithPattern<IUiaInvokePattern>(DialogButton)));
    }

    /// <summary>The denied process's handle, as OpenProcess hands it out: a kill or a close through it is an action.</summary>
    public Mock<IProcessHandle> ProcessHandle { get; private set; } = null!;

    private static void Window(Mock<INativeWindowWrapper> windows, long window, int processId, string processName, string title, string className,
        (int Left, int Top, int Right, int Bottom) rect)
    {
        var handle = (nint)window;
        windows.Setup(w => w.IsWindow(handle)).Returns(true);
        windows.Setup(w => w.IsWindowVisible(handle)).Returns(true);
        windows.Setup(w => w.GetWindowText(handle)).Returns(title);
        windows.Setup(w => w.GetClassName(handle)).Returns(className);
        windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns((uint)processId);
        windows.Setup(w => w.GetWindowThreadId(handle)).Returns((uint)(window & 0xFFFF) + 10);
        windows.Setup(w => w.GetProcessName((uint)processId)).Returns(processName);
        windows.Setup(w => w.GetAncestor(handle, It.IsAny<WindowAncestor>())).Returns(handle);
        windows.Setup(w => w.GetWindowRect(handle)).Returns(rect);
        windows.Setup(w => w.GetExtendedFrameBounds(handle)).Returns((rect.Left + 7, rect.Top, rect.Right - 7, rect.Bottom - 7));
        windows.Setup(w => w.GetWindowLongPtr(handle, -16)).Returns(new nint(0x16CF0000)); // WS_VISIBLE | WS_OVERLAPPEDWINDOW
        windows.Setup(w => w.GetDpiForWindow(handle)).Returns(96u);
        windows.Setup(w => w.RespondsWithin(handle, It.IsAny<uint>())).Returns(true);
        windows.Setup(w => w.GetWindowPlacement(handle)).Returns(new NativeWindowPlacement(1, 0, (0, 0), (-1, -1), rect));
    }
}
