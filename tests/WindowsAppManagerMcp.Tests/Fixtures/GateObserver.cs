using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>One call a <see cref="GateObserver"/> watched, and whether the test's own call held the input gate during it.</summary>
/// <param name="Call">"Interface.Method", with " (gate it hands on)" when the observer took the gate the call was handed.</param>
/// <param name="GateHeld">True when the gate was held by code that runs under the test's tag (<see cref="InputGate.IsHeldBy"/>).</param>
public sealed record GateObservation(string Call, bool GateHeld);

/// <summary>A method a <see cref="GateObserver"/> watches.</summary>
/// <param name="Method">The method's name: every overload of it.</param>
/// <param name="Selects">Which calls count, by their arguments (null: every call), such as the arrangement of apply_layout and
/// not its dry run, which takes no gate.</param>
/// <param name="HandsOnTheGate">The call takes a gate function (CdpGate, or Func&lt;CancellationToken, Task&lt;IDisposable&gt;&gt;)
/// that the service under it enters around the commands that change something: the observer enters it itself and records
/// whether that took the input gate, then forwards the call. A call handed no gate function (null) is recorded with whether
/// the gate is held during it, as a call that is not watched this way.</param>
public sealed record WatchedCall(string Method, Func<object?[], bool>? Selects = null, bool HandsOnTheGate = false);

/// <summary>
/// Watches the calls a tool makes on its services and records, for each, whether the input gate was held by the test's own
/// call (<see cref="InputGate.IsHeldBy"/>, under the tag <see cref="InputGate.TagFlow"/> returned): a decorator
/// (<see cref="DispatchProxy"/>) around a service's mock that forwards every call to it. Tests that run in parallel take the
/// gate too, so only the test's own tag counts.
/// </summary>
public sealed class GateObserver
{
    private static readonly MethodInfo CreateProxy = typeof(DispatchProxy)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(DispatchProxy.Create) && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2);

    private readonly object _tag;
    private readonly ConcurrentQueue<GateObservation> _seen = new();

    /// <param name="tag">The test's flow tag (<see cref="InputGate.TagFlow"/>), taken before the server starts.</param>
    public GateObserver(object tag) => _tag = tag ?? throw new ArgumentNullException(nameof(tag));

    /// <summary>The watched calls, in the order they were made.</summary>
    public IReadOnlyList<GateObservation> Seen => _seen.ToArray();

    /// <summary><paramref name="inner"/>, with <paramref name="watched"/> watched.</summary>
    public T Watch<T>(T inner, params WatchedCall[] watched) where T : class => (T)Watch(typeof(T), inner, watched);

    /// <summary>
    /// <paramref name="inner"/>, an implementation of <paramref name="serviceType"/>, with every method that may change the desktop
    /// watched: all but the property getters and <paramref name="noDesktopChange"/> (reads, checks, waits, the server's own
    /// files), so a member added to the service is watched until it is listed there. A method that takes a gate function is
    /// watched by entering it (<see cref="WatchedCall.HandsOnTheGate"/>); <paramref name="explicitCalls"/> replace the default for
    /// their method, to select calls by their arguments for example.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="noDesktopChange"/> names a method the service does not have.</exception>
    public object WatchEveryChange(Type serviceType, object inner, IReadOnlySet<string> noDesktopChange, IReadOnlyList<WatchedCall> explicitCalls)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(noDesktopChange);
        ArgumentNullException.ThrowIfNull(explicitCalls);
        var methods = MethodsOf(serviceType);
        var unknown = noDesktopChange.Where(name => methods.All(m => m.Name != name)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"{serviceType.Name} has no method {string.Join(", ", unknown)}.", nameof(noDesktopChange));
        }

        var watched = methods
            .Where(m => !(m.IsSpecialName && m.Name.StartsWith("get_", StringComparison.Ordinal)) && !noDesktopChange.Contains(m.Name))
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .Select(g => explicitCalls.FirstOrDefault(c => c.Method == g.Key) ??
                         new WatchedCall(g.Key, HandsOnTheGate: g.Any(m => m.GetParameters().Any(p => IsGateFunctionType(p.ParameterType)))))
            .Concat(explicitCalls.Where(c => noDesktopChange.Contains(c.Method)))
            .ToList();
        return Watch(serviceType, inner, watched);
    }

    /// <summary><paramref name="inner"/>, an implementation of <paramref name="serviceType"/>, with <paramref name="watched"/> watched.</summary>
    public object Watch(Type serviceType, object inner, IReadOnlyList<WatchedCall> watched)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(inner);
        if (!serviceType.IsInterface || !serviceType.IsInstanceOfType(inner))
        {
            throw new ArgumentException($"{inner.GetType()} is no {serviceType}, or {serviceType} is no interface.", nameof(inner));
        }

        var methods = MethodsOf(serviceType);
        var unknown = watched.Select(w => w.Method).Where(name => methods.All(m => m.Name != name)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"{serviceType.Name} has no method {string.Join(", ", unknown)}.", nameof(watched));
        }

        var proxy = (Proxy)CreateProxy.MakeGenericMethod(serviceType, typeof(Proxy)).Invoke(null, null)!;
        proxy.Inner = inner;
        proxy.Watched = watched.ToDictionary(w => w.Method, StringComparer.Ordinal);
        proxy.Observer = this;
        proxy.ServiceName = serviceType.Name;
        return proxy;
    }

    /// <summary>The methods of a service interface: its own and those it inherits from its base interfaces.</summary>
    private static List<MethodInfo> MethodsOf(Type serviceType) =>
        serviceType.GetMethods().Concat(serviceType.GetInterfaces().SelectMany(i => i.GetMethods())).ToList();

    private void Record(string call, object?[] args, WatchedCall watched)
    {
        if (!watched.HandsOnTheGate)
        {
            _seen.Enqueue(new GateObservation(call, InputGate.IsHeldBy(_tag)));
            return;
        }

        var gate = args.OfType<Delegate>().SingleOrDefault(IsGateFunction);
        if (gate == null)
        {
            // Handed no gate to enter around its commands: it must then be called under the gate
            _seen.Enqueue(new GateObservation($"{call} (handed no gate)", InputGate.IsHeldBy(_tag)));
            return;
        }

        var token = args.OfType<CancellationToken>().FirstOrDefault();
        var held = (Task<IDisposable>)gate.DynamicInvoke(token)!;
        using (held.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult())
        {
            _seen.Enqueue(new GateObservation($"{call} (gate it hands on)", InputGate.IsHeldBy(_tag)));
        }
    }

    private static bool IsGateFunction(Delegate candidate) => IsGateFunctionType(candidate.GetType());

    private static bool IsGateFunctionType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!typeof(Delegate).IsAssignableFrom(type) || type.GetMethod("Invoke") is not { } invoke)
        {
            return false;
        }

        return invoke.ReturnType == typeof(Task<IDisposable>) &&
               invoke.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(CancellationToken);
    }

    /// <summary>The decorator: records the watched calls, then forwards every call to the inner service.</summary>
    public class Proxy : DispatchProxy
    {
        internal object Inner { get; set; } = null!;

        internal IReadOnlyDictionary<string, WatchedCall> Watched { get; set; } = null!;

        internal GateObserver Observer { get; set; } = null!;

        internal string ServiceName { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= [];
            if (Watched.TryGetValue(targetMethod.Name, out var watched) && watched.Selects?.Invoke(args) != false)
            {
                Observer.Record($"{ServiceName}.{targetMethod.Name}", args, watched);
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }
}
