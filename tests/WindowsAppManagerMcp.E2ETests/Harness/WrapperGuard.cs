using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>Thrown when a wrapper guard refuses a call. The call never reached the wrapper.</summary>
/// <remarks>
/// Deliberately not an <see cref="InvalidOperationException"/>: the server reads some of those as "the
/// process has exited" or "the window is gone" and could turn a refusal into a reported success.
/// </remarks>
public sealed class E2EGuardException : Exception
{
    public E2EGuardException(string message) : base(message)
    {
    }
}

/// <summary>One call the server makes on a guarded wrapper, as the policy sees it.</summary>
/// <param name="ServiceType">The guarded interface.</param>
/// <param name="Method">The member called.</param>
/// <param name="Args">The arguments, which the policy must not change.</param>
/// <param name="Target">The object behind the guard (for example the process handle a call acts on), or null.</param>
/// <param name="Member">How <see cref="WrapperCatalog"/> classifies the member; null when it does not.</param>
/// <param name="Origin">For a wrapper another wrapper handed out (a pattern object from IUiaTree.GetPattern, a process handle
/// from OpenProcess): the call that handed it out, whose arguments say what it belongs to (the element of a pattern); null
/// otherwise.</param>
public sealed record WrapperCall(
    Type ServiceType, MethodInfo Method, object?[] Args, object? Target, WrapperMember? Member, WrapperCall? Origin = null)
{
    public string Name => $"{ServiceType.Name}.{Method.Name}";
}

/// <summary>Decides whether the server under test may make a call on one of its guarded wrappers.</summary>
public interface IWrapperCallPolicy
{
    /// <summary>Null when the call is allowed; otherwise why it is refused.</summary>
    string? Check(WrapperCall call);
}

/// <summary>
/// Puts a guard in front of the server's native wrappers. The server reaches the desktop only through
/// them (NativeCallGuardTests enforces that for the server assembly), so a guard here sees every
/// window change, every input batch, every process start and kill and every UI Automation read the
/// server makes during a test.
/// </summary>
/// <remarks>
/// The guard is fail-closed: a member is a read only if <see cref="WrapperCatalog"/> says so, and a
/// member the catalog does not list is refused in every tier. A wrapper that hands out another wrapper
/// (a process handle from OpenProcess) hands out a guarded one.
/// </remarks>
public static class WrapperGuard
{
    private const string NativeNamespace = "WindowsAppManagerMcp.Native";

    private static readonly Assembly ServerAssembly = typeof(INativeWindowWrapper).Assembly;

    private static readonly MethodInfo CreateProxyMethod = typeof(DispatchProxy)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(DispatchProxy.Create) && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2);

    /// <summary>
    /// The server's native wrapper interfaces: interfaces of the server assembly named INative* or
    /// *Wrapper, and every server interface a type in the <c>WindowsAppManagerMcp.Native</c> namespace
    /// implements (ITcpPortOwnerResolver, IProcessHandle, ...).
    /// </summary>
    public static IReadOnlySet<Type> ServerNativeWrapperInterfaces { get; } = FindServerNativeWrapperInterfaces();

    /// <summary>True for the server's native wrapper interfaces (<see cref="ServerNativeWrapperInterfaces"/>).</summary>
    public static bool IsNativeWrapper(Type serviceType) => ServerNativeWrapperInterfaces.Contains(serviceType);

    /// <summary>True for the server's own implementations of native calls: the types in its Native namespace.</summary>
    public static bool IsNativeImplementation(Type type) =>
        type.Assembly == ServerAssembly && type.Namespace == NativeNamespace;

    /// <summary>The refusal for a member <see cref="WrapperCatalog"/> does not classify.</summary>
    public static string Unclassified(WrapperCall call) =>
        $"the E2E guard does not know {call.Name}; classify it in {nameof(WrapperCatalog)} first.";

    /// <summary>Wraps <paramref name="inner"/> in a guard that asks <paramref name="policy"/> before every call.</summary>
    public static T Create<T>(T inner, IWrapperCallPolicy policy) where T : class =>
        (T)Create(typeof(T), inner, policy);

    /// <summary>Wraps <paramref name="inner"/>, an implementation of <paramref name="serviceType"/>, in a guard.</summary>
    public static object Create(Type serviceType, object inner, IWrapperCallPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(policy);
        if (!serviceType.IsInstanceOfType(inner))
        {
            throw new ArgumentException($"{inner.GetType()} does not implement {serviceType}.", nameof(inner));
        }

        return CreateProxy(serviceType, inner, policy);
    }

    /// <summary>
    /// An implementation of <paramref name="serviceType"/> that refuses every call with
    /// <paramref name="reason"/>. Nothing stands behind it, so nothing can be reached through it.
    /// </summary>
    public static object CreateRefusing(Type serviceType, string reason)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return CreateProxy(serviceType, inner: null, new RefuseAllCallPolicy(reason));
    }

    /// <summary>
    /// Replaces every native wrapper registration in <paramref name="services"/> with the same
    /// implementation behind a guard, and returns the guarded service types.
    /// </summary>
    public static IReadOnlyList<Type> GuardNativeWrappers(IServiceCollection services, IWrapperCallPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(policy);

        var guarded = new List<Type>();
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.IsKeyedService || !IsNativeWrapper(descriptor.ServiceType))
            {
                continue;
            }

            var serviceType = descriptor.ServiceType;
            services[i] = ServiceDescriptor.Describe(
                serviceType,
                provider => Create(serviceType, CreateInner(provider, descriptor), policy),
                descriptor.Lifetime);
            guarded.Add(serviceType);
        }
        return guarded;
    }

    /// <summary>
    /// Replaces the registrations of <paramref name="serviceType"/> with one that refuses every call
    /// (<see cref="CreateRefusing"/>). The real implementation is never created.
    /// </summary>
    /// <exception cref="InvalidOperationException">The service is not registered.</exception>
    public static void RefuseService(IServiceCollection services, Type serviceType, string reason)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceType);

        var replaced = 0;
        for (var i = 0; i < services.Count; i++)
        {
            if (!services[i].IsKeyedService && services[i].ServiceType == serviceType)
            {
                services[i] = ServiceDescriptor.Singleton(serviceType, _ => CreateRefusing(serviceType, reason));
                replaced++;
            }
        }
        if (replaced == 0)
        {
            throw new InvalidOperationException($"{serviceType.Name} is not registered, so it cannot be refused.");
        }
    }

    /// <summary>Guards a wrapper that the call <paramref name="origin"/> handed out; its calls carry that origin.</summary>
    internal static object CreateHandedOut(Type serviceType, object inner, IWrapperCallPolicy policy, WrapperCall origin) =>
        CreateProxy(serviceType, inner, policy, origin);

    private static object CreateProxy(Type serviceType, object? inner, IWrapperCallPolicy policy, WrapperCall? origin = null)
    {
        var proxy = (GuardProxy)CreateProxyMethod.MakeGenericMethod(serviceType, typeof(GuardProxy)).Invoke(null, null)!;
        proxy.Initialize(serviceType, inner, policy, origin);
        return proxy;
    }

    private static object CreateInner(IServiceProvider provider, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(provider)
        ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);

    private static HashSet<Type> FindServerNativeWrapperInterfaces()
    {
        var interfaces = new HashSet<Type>();
        foreach (var type in ServerAssembly.GetTypes())
        {
            if (type.IsInterface && IsOpaqueToken(type))
            {
                // A member-less interface such as IUiaNode is a handle the wrapper that made it takes back
                // (IUiaTree.Read(node)). It has nothing to call, so there is nothing to guard, and a proxy in
                // its place would break the wrapper that receives it.
                continue;
            }
            if (type.IsInterface)
            {
                if (type.Name.StartsWith("INative", StringComparison.Ordinal) ||
                    type.Name.EndsWith("Wrapper", StringComparison.Ordinal))
                {
                    interfaces.Add(type);
                }
            }
            else if (IsNativeImplementation(type))
            {
                interfaces.UnionWith(type.GetInterfaces().Where(i => i.Assembly == ServerAssembly && !IsOpaqueToken(i)));
            }
        }
        return interfaces;
    }

    /// <summary>True for an interface with no members of its own and none inherited: a marker or opaque handle.</summary>
    internal static bool IsOpaqueToken(Type interfaceType) =>
        interfaceType.IsInterface &&
        interfaceType.GetMembers().Length == 0 &&
        interfaceType.GetInterfaces().All(i => i.GetMembers().Length == 0);
}

/// <summary>The proxy behind <see cref="WrapperGuard"/>. Public only because DispatchProxy derives from it.</summary>
public class GuardProxy : DispatchProxy
{
    private Type _serviceType = typeof(object);
    private object? _inner;
    private IWrapperCallPolicy? _policy;
    private WrapperCall? _origin;

    internal void Initialize(Type serviceType, object? inner, IWrapperCallPolicy policy, WrapperCall? origin = null)
    {
        _serviceType = serviceType;
        _inner = inner;
        _policy = policy;
        _origin = origin;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var call = new WrapperCall(_serviceType, targetMethod, args ?? [], _inner, WrapperCatalog.Find(_serviceType, targetMethod), _origin);

        var refusal = _policy == null ? "the guard has no policy." : _policy.Check(call);
        if (refusal != null)
        {
            throw new E2EGuardException($"E2E guard refused {call.Name}: {refusal}");
        }
        if (_inner == null)
        {
            throw new E2EGuardException($"E2E guard refused {call.Name}: nothing stands behind this guard.");
        }

        object? result;
        try
        {
            result = targetMethod.Invoke(_inner, call.Args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        // A wrapper handed out by a wrapper (the process handle from OpenProcess, a pattern from GetPattern) is guarded in
        // turn, and its calls carry this call, which says what it belongs to
        return result != null && WrapperGuard.IsNativeWrapper(targetMethod.ReturnType)
            ? WrapperGuard.CreateHandedOut(targetMethod.ReturnType, result, _policy!, call)
            : result;
    }
}

/// <summary>Refuses every call, for services the E2E tests must not reach at all.</summary>
public sealed class RefuseAllCallPolicy : IWrapperCallPolicy
{
    private readonly string _reason;

    public RefuseAllCallPolicy(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _reason = reason;
    }

    public string? Check(WrapperCall call) => _reason;
}

/// <summary>
/// The <see cref="DesktopTier.NoInput"/> policy: the server may only read. No input, no window
/// changes, no process starts, closes or kills.
/// </summary>
public sealed class ReadOnlyCallPolicy : IWrapperCallPolicy
{
    public string? Check(WrapperCall call) => call.Member switch
    {
        null => WrapperGuard.Unclassified(call),
        { Kind: WrapperCallKind.Read } => null,
        { Kind: var kind } => $"a DesktopNoInput test may only read and capture; this call would act on the desktop ({kind})."
    };
}

/// <summary>
/// The <see cref="DesktopTier.Input"/> policy: the server may act, but only on the fixture process.
/// Keyboard input is sent only while a fixture window is in the foreground, mouse input only while the
/// cursor is over a fixture window and, when the batch moves the pointer (a click carries an absolute move
/// to its point), only when the move ends over a fixture window; a mouse wheel batch only while a fixture window is
/// also in the foreground, since Windows can send the wheel to the focus window instead of the one under the cursor;
/// window calls only when every window they name is a fixture window,
/// and process closes and kills only on the fixture process. The check runs before every SendInput
/// batch, so a window that comes to the front mid-text stops the typing. No process may be started.
/// Two narrow exceptions serve verified activation (EnsureForeground): a batch of zero-distance mouse
/// moves, which moves nothing and presses nothing, may be sent wherever the cursor is; and the server
/// may attach its own calling thread's input to another thread (never two other threads to each other),
/// because the window calls it makes while attached are checked as window actions.
/// UI Automation actions (a pattern's Invoke, Toggle, SetValue, ..., and IUiaTree.SetFocus, roadmap M3.6) only on an
/// element of the fixture's process, read through the tree that handed out the pattern; an element whose process cannot
/// be read is refused.
/// It is a second, independent layer under the server's own input guard (M1.1, M1.5, InputTargetGuard), so a bug in
/// that guard cannot send input to a window outside the fixture.
/// </summary>
public sealed class FixtureOnlyCallPolicy : IWrapperCallPolicy
{
    /// <summary>MOUSEEVENTF_MOVE: a move, relative by <see cref="InputEvent.Dx"/>, <see cref="InputEvent.Dy"/> unless absolute.</summary>
    internal const uint MouseMoveFlag = 0x0001;

    /// <summary>MOUSEEVENTF_ABSOLUTE: Dx and Dy are normalized coordinates (0..65535).</summary>
    internal const uint MouseAbsoluteFlag = 0x8000;

    /// <summary>MOUSEEVENTF_VIRTUALDESK: the normalized coordinates span the whole virtual desktop.</summary>
    internal const uint MouseVirtualDeskFlag = 0x4000;

    /// <summary>MOUSEEVENTF_WHEEL and MOUSEEVENTF_HWHEEL: a turn of the wheel, by <see cref="InputEvent.MouseData"/>.</summary>
    internal const uint MouseWheelFlags = 0x0800 | 0x1000;

    private readonly IDesktopProbe _probe;
    private readonly int _fixtureProcessId;
    private readonly string _fixtureProcessName;

    public FixtureOnlyCallPolicy(IDesktopProbe probe, int fixtureProcessId, string fixtureProcessName = FixtureContract.ProcessName)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureProcessName);
        if (fixtureProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fixtureProcessId), fixtureProcessId, "Not a process id.");
        }
        _probe = probe;
        _fixtureProcessId = fixtureProcessId;
        _fixtureProcessName = fixtureProcessName;
    }

    public string? Check(WrapperCall call) => call.Member?.Kind switch
    {
        null => WrapperGuard.Unclassified(call),
        WrapperCallKind.Read => null,
        WrapperCallKind.Input => CheckInput(call),
        WrapperCallKind.WindowAction => CheckWindows(call, call.Member!),
        WrapperCallKind.ProcessAction => CheckProcess(call),
        WrapperCallKind.ThreadInputAttach => CheckThreadInputAttach(call),
        WrapperCallKind.UiaAction => CheckUiaAction(call),
        WrapperCallKind.ProcessStart =>
            $"an E2E test may not start processes; teach {nameof(FixtureOnlyCallPolicy)} which starts are safe first.",
        var kind => $"{nameof(FixtureOnlyCallPolicy)} has no rule for {kind} calls."
    };

    private string? CheckInput(WrapperCall call)
    {
        var args = call.Args;
        switch (call.ServiceType == typeof(INativeInputWrapper) ? call.Method.Name : null)
        {
            case nameof(INativeInputWrapper.SendInput):
                var events = args.Length > 0 ? args[0] as IEnumerable<InputEvent> : null;
                if (events == null)
                {
                    return "SendInput without an event list.";
                }
                var batch = events.ToList();
                if (batch.Count > 0 && batch.All(IsZeroDistanceMove))
                {
                    // Moves nothing and presses nothing, so it acts on no window, wherever the cursor is
                    return null;
                }
                if (batch.Any(e => e.Kind != InputEventKind.Mouse) &&
                    RefuseUnlessFixture(_probe.GetForegroundWindow(), "keyboard input: the foreground window") is { } keys)
                {
                    return keys;
                }
                // The wheel goes to the focus window when "scroll inactive windows" is off, so both must be the fixture's
                if (batch.Any(e => e.Kind == InputEventKind.Mouse && (e.Flags & MouseWheelFlags) != 0) &&
                    RefuseUnlessFixture(_probe.GetForegroundWindow(), "mouse wheel input: the foreground window") is { } wheel)
                {
                    return wheel;
                }
                if (batch.Any(e => e.Kind == InputEventKind.Mouse))
                {
                    if (_probe.GetCursorPos() is not { } cursor)
                    {
                        return "mouse input: the cursor position cannot be read.";
                    }
                    if (RefuseUnlessFixture(_probe.WindowFromPoint(cursor.X, cursor.Y),
                            $"mouse input at ({cursor.X}, {cursor.Y}): the window under the cursor") is { } mouse)
                    {
                        return mouse;
                    }

                    // A click carries its own absolute move: the button goes down where that move puts the pointer
                    foreach (var move in batch.Where(e => e.Kind == InputEventKind.Mouse && (e.Flags & MouseMoveFlag) != 0))
                    {
                        if (CheckMoveDestination(move) is { } destination)
                        {
                            return destination;
                        }
                    }
                }
                return null;

            case nameof(INativeInputWrapper.SetCursorPos):
                if (args.Length < 2 || args[0] is not int x || args[1] is not int y)
                {
                    return "SetCursorPos without coordinates.";
                }
                return RefuseUnlessFixture(_probe.WindowFromPoint(x, y), $"moving the cursor to ({x}, {y}): the window there");

            default:
                return $"the E2E input guard does not know {call.Name}; teach {nameof(FixtureOnlyCallPolicy)} about it.";
        }
    }

    private string? CheckWindows(WrapperCall call, WrapperMember member)
    {
        if (member.WindowParameters.Count == 0)
        {
            return $"{call.Method.Name} names no window, so the guard cannot tell that it acts only on the fixture.";
        }

        var parameters = call.Method.GetParameters();
        foreach (var name in member.WindowParameters)
        {
            var index = Array.FindIndex(parameters, p => p.Name == name);
            if (index < 0 || index >= call.Args.Length || call.Args[index] is not nint window)
            {
                return $"{call.Method.Name} has no window handle parameter '{name}', so the guard cannot tell that it acts only on the fixture.";
            }
            if (RefuseUnlessFixture(window, $"{call.Method.Name} on window 0x{window:X} ({name})") is { } refusal)
            {
                return refusal;
            }
        }
        return null;
    }

    /// <summary>
    /// The server may attach only the calling thread's own input queue, and only to another thread: that is
    /// what verified activation does, so its SetForegroundWindow passes the foreground lock. Attaching two
    /// other threads to each other would tie up input queues the server does not own.
    /// </summary>
    private string? CheckThreadInputAttach(WrapperCall call)
    {
        if (call.ServiceType != typeof(INativeThreadInputWrapper) ||
            call.Method.Name != nameof(INativeThreadInputWrapper.AttachThreadInput))
        {
            return $"the E2E guard has no rule for {call.Name}; teach {nameof(FixtureOnlyCallPolicy)} about it.";
        }

        if (call.Args.Length < 2 || call.Args[0] is not uint attachThreadId || call.Args[1] is not uint attachToThreadId)
        {
            return "AttachThreadInput without thread IDs.";
        }

        var callingThreadId = _probe.GetCurrentThreadId();
        if (attachThreadId != callingThreadId)
        {
            return $"AttachThreadInput may only attach the calling thread ({callingThreadId}), not thread {attachThreadId}.";
        }

        return attachToThreadId == 0 || attachToThreadId == callingThreadId
            ? $"AttachThreadInput to thread {attachToThreadId} is not an attach to another thread."
            : null;
    }

    /// <summary>
    /// A UI Automation action acts on one element: allowed only when that element belongs to the fixture's process. The
    /// element is the argument of IUiaTree.SetFocus, or, for a pattern's action, the argument of the GetPattern call that
    /// handed the pattern out; its process is read through that (unguarded) tree, a read.
    /// </summary>
    private string? CheckUiaAction(WrapperCall call)
    {
        (IUiaTree? Tree, IUiaNode? Node) element = call switch
        {
            { ServiceType: var type, Method.Name: nameof(IUiaTree.SetFocus) } when type == typeof(IUiaTree) =>
                (call.Target as IUiaTree, call.Args.Length > 0 ? call.Args[0] as IUiaNode : null),
            { Origin: { Method.Name: nameof(IUiaTree.GetPattern) } origin } when origin.ServiceType == typeof(IUiaTree) =>
                (origin.Target as IUiaTree, origin.Args.Length > 0 ? origin.Args[0] as IUiaNode : null),
            _ => (null, null)
        };
        if (element.Tree is not { } tree || element.Node is not { } node)
        {
            return $"{call.Name} acts on an element the E2E guard cannot identify; teach {nameof(FixtureOnlyCallPolicy)} about it.";
        }

        int processId;
        try
        {
            processId = ReadProcessId(tree, node);
        }
        catch (Exception ex)
        {
            return $"{call.Name}: the guard cannot tell which process the element belongs to ({ex.GetType().Name}: {ex.Message}).";
        }

        return processId == _fixtureProcessId
            ? null
            : $"{call.Name} on an element of process {processId} is not the fixture (process {_fixtureProcessId}).";
    }

    /// <summary>
    /// The element's process. An item its list has not realized (WPF's placeholder) fails the identity read as a whole, so
    /// it is read property by property, as ElementActionService does; an element that is gone fails both reads.
    /// </summary>
    private static int ReadProcessId(IUiaTree tree, IUiaNode node)
    {
        try
        {
            return tree.ReadIdentity(node).ProcessId;
        }
        catch (UiaElementUnavailableException)
        {
            return tree.ReadVirtualizedItem(node).ProcessId;
        }
    }

    private static bool IsZeroDistanceMove(InputEvent e) =>
        e.Kind == InputEventKind.Mouse && e.Flags == MouseMoveFlag && e.Dx == 0 && e.Dy == 0;

    /// <summary>
    /// A mouse move in a batch: only a zero-distance move or an absolute move across the virtual desktop
    /// (the server's click) is understood, and the absolute move must end over the fixture. Windows maps
    /// the normalized coordinate n to the pixel left + n * width / 65536.
    /// </summary>
    private string? CheckMoveDestination(InputEvent move)
    {
        if (move.Flags == MouseMoveFlag && move.Dx == 0 && move.Dy == 0)
        {
            return null;
        }

        if ((move.Flags & (MouseAbsoluteFlag | MouseVirtualDeskFlag)) != (MouseAbsoluteFlag | MouseVirtualDeskFlag))
        {
            return $"a mouse move with flags 0x{move.Flags:X} and distance ({move.Dx}, {move.Dy}) has a destination the E2E " +
                   $"guard cannot work out; teach {nameof(FixtureOnlyCallPolicy)} about it.";
        }

        var screen = _probe.GetVirtualScreen();
        var x = screen.Left + (int)((long)move.Dx * screen.Width / 65536);
        var y = screen.Top + (int)((long)move.Dy * screen.Height / 65536);
        return RefuseUnlessFixture(_probe.WindowFromPoint(x, y), $"mouse input after the move to ({x}, {y}): the window there");
    }

    private string? CheckProcess(WrapperCall call)
    {
        if (call.Target is not IProcessHandle handle)
        {
            return $"{call.Method.Name} acts on no process handle the guard can check.";
        }

        int processId;
        string processName;
        try
        {
            processId = handle.Id;
            processName = handle.ProcessName;
        }
        catch (Exception ex)
        {
            return $"{call.Method.Name}: the guard cannot tell which process the handle is ({ex.GetType().Name}: {ex.Message}).";
        }

        // The name as well as the id, so a fixture pid reused by another process after the fixture exited is refused
        return processId == _fixtureProcessId && string.Equals(processName, _fixtureProcessName, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{call.Method.Name} on process {processId} '{processName}' is not the fixture " +
              $"(process {_fixtureProcessId} '{_fixtureProcessName}').";
    }

    private string? RefuseUnlessFixture(nint window, string what)
    {
        var processId = _probe.GetWindowProcessId(window);
        if (processId == _fixtureProcessId)
        {
            return null;
        }

        var title = window == 0 ? "" : _probe.GetWindowTitle(window);
        return $"{what} (0x{window:X} '{title}', process {processId}) is not the fixture (process {_fixtureProcessId}).";
    }
}
