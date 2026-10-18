using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// An element whose identity read fails (so it is not known whether it is a password field) never has its value read, in each
/// read that returns values: find_ui_elements with detail standard, wait_for_element's value_equals (by criteria and by ref)
/// and read_table's cells. read_element_text and element_action have their own such tests. Over an in-memory UI Automation
/// tree; nothing reads the real desktop.
/// </summary>
public class PasswordFlagUnreadTests
{
    private static readonly nint Handle = 0x7700;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string Secret = "correct horse battery staple";

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly ElementRegistry _registry;
    private readonly FakeUiaNode _window = new("Window", "Sign in");

    public PasswordFlagUnreadTests()
    {
        _windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(300u);
        _windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        _tree.WithWindow(Handle, _window);
    }

    private ElementSearchService Search() => new(_tree, _worker, _registry, _windows.Object);

    /// <summary>A field whose provider fails the identity read: its password flag cannot be read.</summary>
    private static FakeUiaNode UnknownField(string name = "Password") =>
        new("Edit", name)
        {
            AutomationId = name, ActionPatterns = ["Value"], Value = Secret, IdentityException = new InvalidOperationException("provider failed")
        };

    private string Add(FakeUiaNode element)
    {
        _window.With(element);
        return _registry.Register(Handle, _tree.ReadElement(element), new ElementOrigin(_registry.NextGeneration()))!;
    }

    [Fact]
    public async Task Find_DetailStandard_DoesNotReadTheValue_OfAFieldWhosePasswordFlagCannotBeRead()
    {
        // Arrange
        _window.With(UnknownField());

        // Act
        var result = await Search().FindAsync(new ElementQuery(Handle, Name: "Password", Detail: ElementDetail.Standard, Timeout: Timeout));

        // Assert - still found, with what the search read, but no value
        var field = result.Elements!.Single();
        field.Name.Should().Be("Password");
        field.Value.Should().BeNull();
        field.ValueTruncated.Should().BeNull();
        _tree.Reads.Should().NotContain("Value:Password");
        JsonSerializer.Serialize(result).Should().NotContain(Secret);
    }

    private ElementWaitService Waits(IWindowWaitService? windowWait = null) =>
        new(Search(), _registry, windowWait ?? new Mock<IWindowWaitService>().Object, _tree, _worker, _windows.Object,
            delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Wait_ValueEquals_ByRef_DoesNotReadTheValue_OfAFieldWhosePasswordFlagCannotBeRead()
    {
        // Arrange
        var fieldRef = Add(UnknownField());

        // Act
        var result = await Waits().WaitAsync(new ElementWaitRequest
        {
            Ref = fieldRef, Condition = ElementWaitCondition.ValueEquals, Expected = Secret, TimeoutMs = 0
        });

        // Assert - never satisfied by a value it does not read, and never echoes it
        result.Satisfied.Should().BeFalse();
        result.LastObservedState.Should().Contain("its value could not be read");
        _tree.Reads.Should().NotContain("Value:Password");
        JsonSerializer.Serialize(result).Should().NotContain(Secret);
    }

    [Fact]
    public async Task Wait_ValueEquals_ByCriteria_DoesNotReadTheValue_OfAFieldWhosePasswordFlagCannotBeRead()
    {
        // Arrange
        var bodyRef = Add(new FakeUiaNode("Pane", "Body").With(UnknownField()));

        // Act
        var result = await Waits().WaitAsync(new ElementWaitRequest
        {
            Criteria = new ElementQuery(nint.Zero, WithinRef: bodyRef, Name: "Password"),
            Condition = ElementWaitCondition.ValueEquals, Expected = Secret, TimeoutMs = 0
        });

        // Assert
        result.Satisfied.Should().BeFalse();
        _tree.Reads.Should().NotContain("Value:Password");
        JsonSerializer.Serialize(result).Should().NotContain(Secret);
    }

    [Fact]
    public async Task ReadTable_DoesNotReadTheValue_OfACellWhosePasswordFlagCannotBeRead()
    {
        // Arrange - a list of credentials, each row its own cell
        var listRef = Add(new FakeUiaNode("List", "Credentials").With(
            new FakeUiaNode("ListItem", "Mail") { ActionPatterns = ["Value"], Value = "ada@example.com" },
            new FakeUiaNode("ListItem", "Bank")
            {
                ActionPatterns = ["Value"], Value = Secret, IdentityException = new InvalidOperationException("provider failed")
            }));
        var reads = new ElementReadService(_tree, _worker, _registry, Search(), _windows.Object);

        // Act
        var result = await reads.ReadTableAsync(new ElementTableRequest(listRef, 0, 50, 20, Timeout));

        // Assert - null, as a password cell is
        result.Success.Should().BeTrue(result.Error);
        result.Rows!.Select(r => r[0]).Should().Equal("ada@example.com", null);
        _tree.Reads.Should().NotContain("Value:Bank");
        JsonSerializer.Serialize(result).Should().NotContain(Secret);
    }
}
