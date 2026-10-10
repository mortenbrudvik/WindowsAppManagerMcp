using System.Collections.Concurrent;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An in-memory <see cref="IUiaTree"/> for unit tests: no real window or UI Automation provider is
/// read. Build a tree from <see cref="FakeUiaNode"/>s, register windows by handle, and inject
/// failures, delays and call hooks per node.
/// </summary>
public sealed class FakeUiaTree : IUiaTree
{
    private readonly ConcurrentDictionary<nint, FakeUiaNode> _windows = new();
    private readonly ConcurrentQueue<string> _reads = new();

    /// <summary>What <see cref="IsAvailable"/> returns.</summary>
    public bool Available { get; set; } = true;

    /// <summary>What <see cref="GetFocusedElement"/> returns.</summary>
    public FakeUiaNode? Focused { get; set; }

    /// <summary>Thrown by <see cref="GetFocusedElement"/> when set.</summary>
    public Exception? FocusedException { get; set; }

    /// <summary>What <see cref="FromPoint"/> returns for a point; null by default.</summary>
    public Func<int, int, FakeUiaNode?> AtPoint { get; set; } = (_, _) => null;

    /// <summary>Thrown by <see cref="FromHandle"/> when set.</summary>
    public Exception? FromHandleException { get; set; }

    /// <summary>Every property read, as "Read:Name", "Interaction:Name", "Interactable:Name", ... in call order.</summary>
    public IReadOnlyList<string> Reads => _reads.ToList();

    /// <summary>The names of the nodes whose basic properties were read.</summary>
    public IReadOnlyList<string> ReadNodes => Reads.Where(r => r.StartsWith("Read:")).Select(r => r[5..]).ToList();

    /// <summary>Registers <paramref name="window"/> as the node of <paramref name="handle"/>.</summary>
    public FakeUiaTree WithWindow(nint handle, FakeUiaNode window)
    {
        _windows[handle] = window;
        return this;
    }

    public bool IsAvailable() => Available;

    public IUiaNode? FromHandle(nint windowHandle)
    {
        if (FromHandleException != null)
        {
            throw FromHandleException;
        }

        return _windows.TryGetValue(windowHandle, out var window) ? window : null;
    }

    public IUiaNode? FromPoint(int x, int y) => AtPoint(x, y);

    public IUiaNode? GetFocusedElement()
    {
        if (FocusedException != null)
        {
            throw FocusedException;
        }

        return Focused;
    }

    public IUiaNode? GetParent(IUiaNode node)
    {
        var fake = Fake(node);
        ThrowIfSet(fake.ParentException);
        return fake.Parent;
    }

    public IReadOnlyList<IUiaNode> GetChildren(IUiaNode node, CancellationToken cancellationToken)
    {
        var fake = Fake(node);
        fake.OnGetChildren?.Invoke();
        ThrowIfSet(fake.ChildrenException);
        return fake.Children.ToList();
    }

    public UiaNodeProperties Read(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Read:{fake.Name}");
        fake.OnRead?.Invoke();
        ThrowIfSet(fake.ReadException);
        return new UiaNodeProperties(fake.Name, fake.ControlType, fake.AutomationId, fake.Bounds, fake.IsEnabled, fake.IsOffscreen);
    }

    public UiaNodeInteraction ReadInteraction(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Interaction:{fake.Name}");
        ThrowIfSet(fake.InteractionException);
        return new UiaNodeInteraction(fake.IsKeyboardFocusable, fake.HasKeyboardFocus, fake.ActionPatterns);
    }

    public bool IsInteractable(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Interactable:{fake.Name}");
        ThrowIfSet(fake.InteractionException);
        return fake.IsKeyboardFocusable || fake.ActionPatterns.Count > 0;
    }

    public UiaNodeIdentity ReadIdentity(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Identity:{fake.Name}");
        ThrowIfSet(fake.IdentityException);
        return new UiaNodeIdentity(fake.ClassName, fake.FrameworkId, fake.ProcessId, fake.NativeWindowHandle, fake.IsPassword);
    }

    public string? ReadValue(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Value:{fake.Name}");
        return fake.Value;
    }

    private static void ThrowIfSet(Exception? exception)
    {
        if (exception != null)
        {
            throw exception;
        }
    }

    private static FakeUiaNode Fake(IUiaNode node) =>
        node as FakeUiaNode ?? throw new ArgumentException("Not a FakeUiaNode", nameof(node));

    /// <summary>The exception a real tree throws for an element that disappeared.</summary>
    public static UiaElementUnavailableException Vanished(string what = "element") =>
        new($"The {what} is no longer available.");
}

/// <summary>A node of <see cref="FakeUiaTree"/>. Every property can be set with an object initializer.</summary>
public sealed class FakeUiaNode : IUiaNode
{
    private readonly List<FakeUiaNode> _children = [];

    public FakeUiaNode(string controlType, string? name = null)
    {
        ControlType = controlType;
        Name = name;
    }

    public string ControlType { get; init; }
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public UiaRect Bounds { get; init; } = new(0, 0, 100, 30);
    public bool IsEnabled { get; init; } = true;
    public bool IsOffscreen { get; init; }

    public bool IsKeyboardFocusable { get; init; }
    public bool HasKeyboardFocus { get; init; }
    public IReadOnlyList<string> ActionPatterns { get; init; } = [];

    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public int ProcessId { get; init; }
    public nint NativeWindowHandle { get; init; }
    public bool IsPassword { get; init; }
    public string? Value { get; init; }

    public Exception? ReadException { get; set; }
    public Exception? InteractionException { get; set; }
    public Exception? IdentityException { get; set; }
    public Exception? ChildrenException { get; set; }
    public Exception? ParentException { get; set; }

    /// <summary>Runs when the node's basic properties are read, before they are returned.</summary>
    public Action? OnRead { get; set; }

    /// <summary>Runs when the node's children are read, before they are returned.</summary>
    public Action? OnGetChildren { get; set; }

    public FakeUiaNode? Parent { get; private set; }

    public IReadOnlyList<FakeUiaNode> Children => _children;

    /// <summary>Appends children and returns this node, so trees can be written as nested expressions.</summary>
    public FakeUiaNode With(params FakeUiaNode[] children)
    {
        foreach (var child in children)
        {
            child.Parent = this;
            _children.Add(child);
        }

        return this;
    }

    public override string ToString() => $"{ControlType} '{Name}'";
}
