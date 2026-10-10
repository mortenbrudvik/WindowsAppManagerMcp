using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>Thrown when a wrapper guard refuses a native call. The call never reached the native wrapper.</summary>
public sealed class E2EGuardException : InvalidOperationException
{
    public E2EGuardException(string message) : base(message)
    {
    }
}

/// <summary>Decides whether the server under test may make a call on one of its native wrappers.</summary>
public interface IWrapperCallPolicy
{
    /// <summary>Null when the call is allowed; otherwise why it is refused.</summary>
    string? Check(Type serviceType, MethodInfo method, object?[] args);
}

/// <summary>
/// Puts a guard in front of the server's native wrappers. The server reaches the desktop only through
/// them (NativeCallGuardTests enforces that for the server assembly), so a guard here sees every
/// window change, every input batch and every UI Automation action the server makes during a test.
/// </summary>
/// <remarks>
/// The guard is fail-closed: a call is a read only if its name says so (<see cref="IsRead"/>), and a
/// wrapper member the policy does not know is refused. A wrapper or member added by later work is
/// therefore refused until a policy allows it, instead of silently reaching the desktop.
/// </remarks>
public static class WrapperGuard
{
    private static readonly string[] ReadPrefixes = ["Get", "Is", "Enum", "TryGet", "Capture"];

    private static readonly MethodInfo CreateProxyMethod = typeof(DispatchProxy)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(DispatchProxy.Create) && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2);

    /// <summary>
    /// True for wrapper members that only read: names starting with Get, Is, Enum, TryGet or Capture
    /// (screen captures), and property getters.
    /// </summary>
    public static bool IsRead(MethodInfo method) =>
        (method.IsSpecialName && method.Name.StartsWith("get_", StringComparison.Ordinal)) ||
        ReadPrefixes.Any(prefix => method.Name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// True for the server's native wrapper interfaces: interfaces of the server assembly named
    /// INative* or *Wrapper (INativeWindowWrapper, INativeInputWrapper, IUIAutomationWrapper,
    /// IScreenCaptureWrapper, ...).
    /// </summary>
    public static bool IsNativeWrapper(Type serviceType) =>
        serviceType.IsInterface &&
        serviceType.Assembly == typeof(INativeWindowWrapper).Assembly &&
        (serviceType.Name.StartsWith("INative", StringComparison.Ordinal) ||
         serviceType.Name.EndsWith("Wrapper", StringComparison.Ordinal));

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

        var proxy = (GuardProxy)CreateProxyMethod.MakeGenericMethod(serviceType, typeof(GuardProxy)).Invoke(null, null)!;
        proxy.Initialize(serviceType, inner, policy);
        return proxy;
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

    private static object CreateInner(IServiceProvider provider, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(provider)
        ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
}

/// <summary>The proxy behind <see cref="WrapperGuard"/>. Public only because DispatchProxy derives from it.</summary>
public class GuardProxy : DispatchProxy
{
    private Type _serviceType = typeof(object);
    private object _inner = new();
    private IWrapperCallPolicy? _policy;

    internal void Initialize(Type serviceType, object inner, IWrapperCallPolicy policy)
    {
        _serviceType = serviceType;
        _inner = inner;
        _policy = policy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var arguments = args ?? [];

        var refusal = _policy?.Check(_serviceType, targetMethod, arguments)
            ?? (_policy == null ? "the guard has no policy" : null);
        if (refusal != null)
        {
            throw new E2EGuardException($"E2E guard refused {_serviceType.Name}.{targetMethod.Name}: {refusal}");
        }

        try
        {
            return targetMethod.Invoke(_inner, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>
/// The <see cref="DesktopTier.NoInput"/> policy: the server may only read. No input, no window
/// changes, no UI Automation actions.
/// </summary>
public sealed class ReadOnlyCallPolicy : IWrapperCallPolicy
{
    public string? Check(Type serviceType, MethodInfo method, object?[] args) =>
        WrapperGuard.IsRead(method)
            ? null
            : "a DesktopNoInput test may only read and capture; this call would act on the desktop.";
}

/// <summary>
/// The <see cref="DesktopTier.Input"/> policy: the server may act, but only on the fixture process.
/// Keyboard input is sent only while a fixture window is in the foreground, mouse input only while the
/// cursor is over a fixture window, and window calls only for fixture windows. The check runs before
/// every SendInput batch, so a window that comes to the front mid-text stops the typing.
/// This is the harness's stand-in for the server's own target guard (M1.1, M1.5), which does not exist yet.
/// </summary>
public sealed class FixtureOnlyCallPolicy : IWrapperCallPolicy
{
    private readonly IDesktopProbe _probe;
    private readonly int _fixtureProcessId;

    public FixtureOnlyCallPolicy(IDesktopProbe probe, int fixtureProcessId)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (fixtureProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fixtureProcessId), fixtureProcessId, "Not a process id.");
        }
        _probe = probe;
        _fixtureProcessId = fixtureProcessId;
    }

    public string? Check(Type serviceType, MethodInfo method, object?[] args)
    {
        if (WrapperGuard.IsRead(method))
        {
            return null;
        }

        return serviceType == typeof(INativeInputWrapper)
            ? CheckInput(method, args)
            : CheckWindowCall(method, args);
    }

    private string? CheckInput(MethodInfo method, object?[] args)
    {
        switch (method.Name)
        {
            case nameof(INativeInputWrapper.SendInput):
                var events = args.Length > 0 ? args[0] as IEnumerable<InputEvent> : null;
                if (events == null)
                {
                    return "SendInput without an event list.";
                }
                var batch = events.ToList();
                if (batch.Any(e => e.Kind != InputEventKind.Mouse) &&
                    RefuseUnlessFixture(_probe.GetForegroundWindow(), "keyboard input: the foreground window") is { } keys)
                {
                    return keys;
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
                }
                return null;

            case nameof(INativeInputWrapper.SetCursorPos):
                if (args.Length < 2 || args[0] is not int x || args[1] is not int y)
                {
                    return "SetCursorPos without coordinates.";
                }
                return RefuseUnlessFixture(_probe.WindowFromPoint(x, y), $"moving the cursor to ({x}, {y}): the window there");

            default:
                return $"the E2E input guard does not know {method.Name}; teach {nameof(FixtureOnlyCallPolicy)} about it.";
        }
    }

    private string? CheckWindowCall(MethodInfo method, object?[] args)
    {
        var parameters = method.GetParameters();
        var windowIndex = Array.FindIndex(parameters, p => p.ParameterType == typeof(nint));
        if (windowIndex < 0 || windowIndex >= args.Length || args[windowIndex] is not nint window)
        {
            return $"{method.Name} names no window, so the guard cannot tell that it acts only on the fixture.";
        }
        return RefuseUnlessFixture(window, $"{method.Name} on window 0x{window:X}");
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
