using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// The UIA3 adapter (roadmap M3.2) over mocks of the UI Automation COM client's interfaces: no real client is
/// created and no window is read.
/// </summary>
public class Uia3TreeTests
{
    private readonly Mock<IUIAutomation2> _automation = new();
    private readonly Mock<IUIAutomationElement> _root = new();
    private readonly Mock<IUIAutomationCondition> _controlView = new();
    private readonly Mock<IUIAutomationTreeWalker> _walker = new();
    private readonly List<Mock<IUIAutomationCacheRequest>> _requests = [];
    private int _created;
    private int _nextHandle = 0x1000;

    public Uia3TreeTests()
    {
        _automation.Setup(a => a.GetRootElement()).Returns(_root.Object);
        _automation.SetupGet(a => a.ControlViewCondition).Returns(_controlView.Object);
        _automation.SetupGet(a => a.ControlViewWalker).Returns(_walker.Object);
        _automation.Setup(a => a.CreateCacheRequest()).Returns(() =>
        {
            var request = new Mock<IUIAutomationCacheRequest>();
            request.SetupProperty(r => r.TreeScope);
            _requests.Add(request);
            return request.Object;
        });
    }

    private Uia3Tree CreateSut() => new(() =>
    {
        _created++;
        return _automation.Object;
    });

    /// <summary>A node of the adapter, wrapping <paramref name="element"/>, as FromHandle returns it.</summary>
    private IUiaNode NodeOf(Uia3Tree sut, Mock<IUIAutomationElement> element)
    {
        var handle = new nint(++_nextHandle);
        _automation.Setup(a => a.ElementFromHandle(handle)).Returns(element.Object);
        return sut.FromHandle(handle)!;
    }

    /// <summary>A cached element as FindAllBuildCache returns it.</summary>
    private static Mock<IUIAutomationElement> Cached(
        string? name,
        object? controlTypeId,
        string? localizedControlType = null,
        double[]? bounds = null,
        bool enabled = true,
        bool offscreen = false,
        bool focusable = false,
        int[]? runtimeId = null,
        params int[] availablePatternProperties)
    {
        var element = new Mock<IUIAutomationElement>();
        element.Setup(e => e.GetCachedPropertyValue(It.IsAny<int>())).Returns(NotSupported);
        element.Setup(e => e.GetCachedPropertyValue(UiaIds.NameProperty)).Returns(name!);
        element.Setup(e => e.GetCachedPropertyValue(UiaIds.ControlTypeProperty)).Returns(controlTypeId!);
        element.Setup(e => e.GetCachedPropertyValue(UiaIds.LocalizedControlTypeProperty)).Returns(localizedControlType!);
        element.Setup(e => e.GetCachedPropertyValue(UiaIds.BoundingRectangleProperty)).Returns(bounds!);
        element.Setup(e => e.GetCachedPropertyValue(UiaIds.IsEnabledProperty)).Returns(enabled);
        element.Setup(e => e.GetCachedPropertyValue(UiaIds.IsOffscreenProperty)).Returns(offscreen);
        element.Setup(e => e.GetCachedPropertyValue(UiaIds.IsKeyboardFocusableProperty)).Returns(focusable);
        element.Setup(e => e.GetCachedPropertyValue(UiaIds.RuntimeIdProperty)).Returns(runtimeId!);
        foreach (var property in availablePatternProperties)
        {
            element.Setup(e => e.GetCachedPropertyValue(property)).Returns(true);
        }

        return element;
    }

    /// <summary>What the COM client returns for a property the provider does not support.</summary>
    private static readonly object NotSupported = new();

    private static Mock<IUIAutomationElementArray> ArrayOf(params Mock<IUIAutomationElement>[] elements)
    {
        var array = new Mock<IUIAutomationElementArray>();
        array.SetupGet(a => a.Length).Returns(elements.Length);
        for (var i = 0; i < elements.Length; i++)
        {
            var element = elements[i].Object;
            array.Setup(a => a.GetElement(i)).Returns(element);
        }

        return array;
    }

    #region The client

    [Fact]
    public void Engine_IsUia3()
    {
        CreateSut().Engine.Should().Be(UiaEngine.Uia3);
    }

    [Fact]
    public void Constructing_CreatesNoClient()
    {
        // The server resolves the tree at start; UI Automation must not be touched before the first read
        _ = CreateSut();

        _created.Should().Be(0);
    }

    [Fact]
    public void FirstUse_CreatesTheClientOnce_WithTheConnectionAndTransactionTimeouts()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.IsAvailable().Should().BeTrue();
        sut.IsAvailable().Should().BeTrue();

        // Assert - hard deadlines for a provider that does not answer (a hung app, an Invoke blocked by its own modal)
        _created.Should().Be(1);
        _automation.VerifySet(a => a.ConnectionTimeout = 1000u, Times.Once);
        _automation.VerifySet(a => a.TransactionTimeout = 5000u, Times.Once);
        Uia3Tree.ConnectionTimeoutMs.Should().Be(1000u);
        Uia3Tree.TransactionTimeoutMs.Should().Be(5000u);
    }

    [Fact]
    public void IsAvailable_WhenTheClientCannotBeCreated_IsFalse_AndTheNextCallTriesAgain()
    {
        // Arrange - REGDB_E_CLASSNOTREG, for example
        var attempts = 0;
        var sut = new Uia3Tree(() =>
        {
            attempts++;
            throw new COMException("Class not registered", unchecked((int)0x80040154));
        });

        // Act & Assert
        sut.IsAvailable().Should().BeFalse();
        sut.IsAvailable().Should().BeFalse();
        attempts.Should().Be(2);
    }

    [Fact]
    public void TheWalksCacheRequest_ReadsTheWalksPropertiesAndEveryPatternsAvailability_InTheControlView()
    {
        // Act
        CreateSut().IsAvailable();

        // Assert
        var walk = _requests[0];
        foreach (var property in new[]
                 {
                     UiaIds.NameProperty, UiaIds.ControlTypeProperty, UiaIds.LocalizedControlTypeProperty, UiaIds.AutomationIdProperty,
                     UiaIds.ClassNameProperty, UiaIds.BoundingRectangleProperty, UiaIds.IsEnabledProperty, UiaIds.IsOffscreenProperty,
                     UiaIds.IsKeyboardFocusableProperty, UiaIds.RuntimeIdProperty, UiaIds.FrameworkIdProperty
                 }.Concat(UiaIds.PatternAvailability.Select(p => p.Property)))
        {
            walk.Verify(r => r.AddProperty(property), Times.Once, $"property {property}");
        }

        walk.VerifySet(r => r.TreeFilter = _controlView.Object);
        walk.VerifySet(r => r.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full);
        walk.Object.TreeScope.Should().Be(TreeScope.TreeScope_Element);
        _requests[1].Object.TreeScope.Should().Be(TreeScope.TreeScope_Subtree, "the subtree read caches the whole subtree");
    }

    #endregion

    #region Lookups

    [Fact]
    public void FromHandle_OfAHandleThatIsNoWindow_IsNull()
    {
        // E_INVALIDARG becomes an ArgumentException, UIA_E_ELEMENTNOTAVAILABLE a COMException
        _automation.Setup(a => a.ElementFromHandle(new nint(1))).Throws(new ArgumentException("E_INVALIDARG"));
        _automation.Setup(a => a.ElementFromHandle(new nint(2))).Throws(new COMException("gone", UiaComErrors.ElementNotAvailable));
        var sut = CreateSut();

        sut.FromHandle(new nint(1)).Should().BeNull();
        sut.FromHandle(new nint(2)).Should().BeNull();
    }

    [Fact]
    public void GetParent_OfATopLevelElement_IsNull()
    {
        // Arrange - the parent is the desktop root
        var sut = CreateSut();
        var window = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, window);
        _walker.Setup(w => w.GetParentElement(window.Object)).Returns(_root.Object);
        _automation.Setup(a => a.CompareElements(_root.Object, _root.Object)).Returns(1);

        // Act & Assert
        sut.GetParent(node).Should().BeNull();
    }

    [Fact]
    public void Reads_RejectNodesFromAnotherTree()
    {
        var sut = CreateSut();
        var foreign = new FakeUiaNode("Button", "Fake");

        sut.Invoking(t => t.Read(foreign)).Should().Throw<ArgumentException>();
        sut.Invoking(t => t.GetChildElements(foreign, CancellationToken.None)).Should().Throw<ArgumentException>();
        sut.Invoking(t => t.GetPattern<IUiaInvokePattern>(foreign)).Should().Throw<ArgumentException>();
    }

    #endregion

    #region Cached children

    [Fact]
    public void GetChildElements_FetchesTheLevelInOneCall_WithItsCachedProperties()
    {
        // Arrange
        var sut = CreateSut();
        var window = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, window);
        var save = Cached("Save", 50000, bounds: [812.5, 44, 80, 24], enabled: false, focusable: true, runtimeId: [42, 7],
            availablePatternProperties: [UiaIds.IsInvokePatternAvailableProperty, UiaIds.IsLegacyIAccessiblePatternAvailableProperty]);
        save.Setup(e => e.GetCachedPropertyValue(UiaIds.AutomationIdProperty)).Returns("SaveBtn");
        save.Setup(e => e.GetCachedPropertyValue(UiaIds.ClassNameProperty)).Returns("Button");
        save.Setup(e => e.GetCachedPropertyValue(UiaIds.FrameworkIdProperty)).Returns("Win32");
        var children = ArrayOf(save);
        window.Setup(e => e.FindAllBuildCache(TreeScope.TreeScope_Children, _controlView.Object, It.IsAny<IUIAutomationCacheRequest>()))
            .Returns(children.Object);

        // Act
        var element = sut.GetChildElements(node, CancellationToken.None).Single();

        // Assert
        element.Properties.Should().Be(new UiaNodeProperties("Save", "Button", "SaveBtn", new UiaRect(812.5, 44, 80, 24), false, false, "Win32"));
        element.ClassName.Should().Be("Button");
        element.IsKeyboardFocusable.Should().BeTrue();
        element.Patterns.Should().Be(UiaPatterns.Invoke | UiaPatterns.LegacyIAccessible);
        element.IsInteractable.Should().BeTrue();
        element.RuntimeId.Should().Equal(42, 7);
        element.ControlTypeId.Should().Be(50000);
        window.Verify(e => e.FindAllBuildCache(TreeScope.TreeScope_Children, _controlView.Object, _requests[0].Object), Times.Once);
    }

    [Theory]
    [InlineData(50040, null, "AppBar")]
    [InlineData(50039, null, "SemanticZoom")]
    [InlineData(50033, "rute", "Pane")]
    [InlineData(50099, "fancy widget", "FancyWidget")]
    [InlineData(50099, null, "Unknown")]
    public void GetChildElements_NamesTheRawControlTypeId_InEveryLanguage(int controlTypeId, string? localized, string expected)
    {
        // Arrange - a Norwegian Windows calls an app bar "appfelt"; the id is the same everywhere
        var sut = CreateSut();
        var window = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, window);
        var children = ArrayOf(Cached("x", controlTypeId, localized));
        window.Setup(e => e.FindAllBuildCache(It.IsAny<TreeScope>(), It.IsAny<IUIAutomationCondition>(), It.IsAny<IUIAutomationCacheRequest>()))
            .Returns(children.Object);

        // Act
        var element = sut.GetChildElements(node, CancellationToken.None).Single();

        // Assert
        element.Properties.ControlType.Should().Be(expected);
        element.ControlTypeId.Should().Be(controlTypeId);
    }

    [Fact]
    public void GetChildElements_PropertiesTheProviderDoesNotSupport_ReadAsEmpty()
    {
        // Arrange - every property reads as the "not supported" object; an element without geometry is (0, 0, 0, 0)
        var sut = CreateSut();
        var window = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, window);
        var bare = Cached(null, null, bounds: [0, 0, 0, 0]);
        bare.Setup(e => e.GetCachedPropertyValue(UiaIds.NameProperty)).Returns(NotSupported);
        bare.Setup(e => e.GetCachedPropertyValue(UiaIds.ControlTypeProperty)).Returns(NotSupported);
        bare.Setup(e => e.GetCachedPropertyValue(UiaIds.IsEnabledProperty)).Returns(NotSupported);
        var children = ArrayOf(bare);
        window.Setup(e => e.FindAllBuildCache(It.IsAny<TreeScope>(), It.IsAny<IUIAutomationCondition>(), It.IsAny<IUIAutomationCacheRequest>()))
            .Returns(children.Object);

        // Act
        var element = sut.GetChildElements(node, CancellationToken.None).Single();

        // Assert
        element.Properties.Should().Be(new UiaNodeProperties(null, "Unknown", null, UiaRect.Empty, false, false));
        element.Properties.Bounds.ToBounds().Should().BeNull("an element without a rectangle has no bounds in either engine");
        element.Patterns.Should().Be(UiaPatterns.None);
        element.RuntimeId.Should().BeNull();
        element.ControlTypeId.Should().BeNull();
    }

    [Fact]
    public void GetChildElements_OfAnElementThatIsGone_ThrowsTheSeamsException()
    {
        // Arrange
        var sut = CreateSut();
        var window = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, window);
        window.Setup(e => e.FindAllBuildCache(It.IsAny<TreeScope>(), It.IsAny<IUIAutomationCondition>(), It.IsAny<IUIAutomationCacheRequest>()))
            .Throws(new COMException("gone", UiaComErrors.ElementNotAvailable));

        // Act & Assert
        sut.Invoking(t => t.GetChildElements(node, CancellationToken.None)).Should().Throw<UiaElementUnavailableException>();
    }

    [Fact]
    public void GetChildElements_AfterTheWalkWasAskedToStop_MakesNoCall()
    {
        var sut = CreateSut();
        var window = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, window);

        sut.Invoking(t => t.GetChildElements(node, new CancellationToken(canceled: true))).Should().Throw<OperationCanceledException>();
        window.Verify(e => e.FindAllBuildCache(It.IsAny<TreeScope>(), It.IsAny<IUIAutomationCondition>(), It.IsAny<IUIAutomationCacheRequest>()), Times.Never);
    }

    [Fact]
    public void Read_FetchesTheElementsPropertiesInOneCall()
    {
        // Arrange
        var sut = CreateSut();
        var window = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, window);
        var cached = Cached("Editor", 50032, bounds: [-1920, 0, 800, 600]);
        window.Setup(e => e.BuildUpdatedCache(It.IsAny<IUIAutomationCacheRequest>())).Returns(cached.Object);

        // Act
        var properties = sut.Read(node);

        // Assert
        properties.Should().Be(new UiaNodeProperties("Editor", "Window", null, new UiaRect(-1920, 0, 800, 600), true, false));
        window.Verify(e => e.BuildUpdatedCache(_requests[0].Object), Times.Once);
        window.Verify(e => e.GetCurrentPropertyValue(It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region Element reads for the registry and find_ui_elements (roadmap M3.4)

    [Fact]
    public void ReadElement_ReadsTheWalksPropertiesAndTheRuntimeIdInOneCallToTheProvider()
    {
        // Arrange
        var sut = CreateSut();
        var element = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, element);
        var cached = Cached("Save", 50000, runtimeId: [42, 7], availablePatternProperties: UiaIds.IsInvokePatternAvailableProperty);
        cached.Setup(e => e.GetCachedPropertyValue(UiaIds.ClassNameProperty)).Returns("Button");
        element.Setup(e => e.BuildUpdatedCache(It.IsAny<IUIAutomationCacheRequest>())).Returns(cached.Object);

        // Act
        var read = sut.ReadElement(node);

        // Assert - BuildUpdatedCache always asks the provider, so it tells a live element from a gone one
        read.Properties.Name.Should().Be("Save");
        read.ClassName.Should().Be("Button");
        read.RuntimeId.Should().Equal(42, 7);
        read.Patterns.Should().Be(UiaPatterns.Invoke);
        element.Verify(e => e.BuildUpdatedCache(_requests[0].Object), Times.Once);
    }

    [Fact]
    public void ReadElement_OfAnElementThatIsGone_ThrowsTheSeamsException()
    {
        // Arrange
        var sut = CreateSut();
        var element = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, element);
        element.Setup(e => e.BuildUpdatedCache(It.IsAny<IUIAutomationCacheRequest>()))
            .Throws(new COMException("Element not available", unchecked((int)0x80040201)));

        // Act / Assert
        FluentActions.Invoking(() => sut.ReadElement(node)).Should().Throw<UiaElementUnavailableException>();
    }

    [Fact]
    public void GetClickablePoint_ReturnsThePointTheProviderGives()
    {
        // Arrange
        var sut = CreateSut();
        var element = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, element);
        var point = new tagPOINT { x = -1200, y = 340 };
        element.Setup(e => e.GetClickablePoint(out point)).Returns(1);

        // Act / Assert
        sut.GetClickablePoint(node).Should().Be(new UiaPoint(-1200, 340));
    }

    [Fact]
    public void GetClickablePoint_WhenTheProviderHasNone_IsNull()
    {
        // Arrange
        var sut = CreateSut();
        var element = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, element);
        var point = new tagPOINT();
        element.Setup(e => e.GetClickablePoint(out point)).Returns(0);

        // Act / Assert
        sut.GetClickablePoint(node).Should().BeNull();
    }

    [Fact]
    public void ReadDescription_ReadsHelpTextKeysAndLocalizedTypeInOneCall()
    {
        // Arrange
        var sut = CreateSut();
        var element = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, element);
        var cached = new Mock<IUIAutomationElement>();
        cached.Setup(e => e.GetCachedPropertyValue(It.IsAny<int>())).Returns(NotSupported);
        cached.Setup(e => e.GetCachedPropertyValue(UiaIds.HelpTextProperty)).Returns("Saves the file");
        cached.Setup(e => e.GetCachedPropertyValue(UiaIds.AccessKeyProperty)).Returns("Alt+S");
        cached.Setup(e => e.GetCachedPropertyValue(UiaIds.AcceleratorKeyProperty)).Returns("Ctrl+S");
        cached.Setup(e => e.GetCachedPropertyValue(UiaIds.LocalizedControlTypeProperty)).Returns("button");
        element.Setup(e => e.BuildUpdatedCache(It.IsAny<IUIAutomationCacheRequest>())).Returns(cached.Object);

        // Act
        var description = sut.ReadDescription(node);

        // Assert
        description.Should().Be(new UiaNodeDescription("Saves the file", "Alt+S", "Ctrl+S", "button"));
        element.Verify(e => e.BuildUpdatedCache(It.IsAny<IUIAutomationCacheRequest>()), Times.Once);
        element.Verify(e => e.GetCurrentPropertyValue(It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region Identity and interaction reads (the managed client reads these from element.Current instead)

    /// <summary>
    /// An element of <paramref name="sut"/> whose BuildUpdatedCache returns <paramref name="cached"/>, and the cache requests
    /// it was asked with, in order.
    /// </summary>
    private (IUiaNode Node, Mock<IUIAutomationElement> Element, List<IUIAutomationCacheRequest> Requests) CachedBy(
        Uia3Tree sut, Mock<IUIAutomationElement> cached)
    {
        var element = new Mock<IUIAutomationElement>();
        var requests = new List<IUIAutomationCacheRequest>();
        element.Setup(e => e.BuildUpdatedCache(It.IsAny<IUIAutomationCacheRequest>()))
            .Callback<IUIAutomationCacheRequest>(requests.Add)
            .Returns(cached.Object);
        return (NodeOf(sut, element), element, requests);
    }

    [Fact]
    public void ReadIdentity_ReadsClassFrameworkProcessHandleAndPasswordFlag_InOneCall()
    {
        // Arrange
        var sut = CreateSut();
        var cached = new Mock<IUIAutomationElement>();
        cached.Setup(e => e.GetCachedPropertyValue(It.IsAny<int>())).Returns(NotSupported);
        var (node, element, requests) = CachedBy(sut, cached);

        // Act
        sut.ReadIdentity(node);

        // Assert - the identity request is one cross-process call for all five
        var request = _requests.Single(r => r.Object == requests.Single());
        foreach (var property in new[]
                 {
                     UiaIds.ClassNameProperty, UiaIds.FrameworkIdProperty, UiaIds.ProcessIdProperty,
                     UiaIds.NativeWindowHandleProperty, UiaIds.IsPasswordProperty
                 })
        {
            request.Verify(r => r.AddProperty(property), Times.Once, $"property {property}");
        }

        element.Verify(e => e.GetCurrentPropertyValue(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void ReadIdentity_MapsTheCachedValues()
    {
        // Arrange - a password box of process 4242 that is a window of its own
        var sut = CreateSut();
        var cached = new Mock<IUIAutomationElement>();
        cached.Setup(e => e.GetCachedPropertyValue(It.IsAny<int>())).Returns(NotSupported);
        cached.Setup(e => e.GetCachedPropertyValue(UiaIds.ClassNameProperty)).Returns("PasswordBox");
        cached.Setup(e => e.GetCachedPropertyValue(UiaIds.FrameworkIdProperty)).Returns("WPF");
        cached.Setup(e => e.GetCachedPropertyValue(UiaIds.ProcessIdProperty)).Returns(4242);
        cached.Setup(e => e.GetCachedPropertyValue(UiaIds.NativeWindowHandleProperty)).Returns(0x1234);
        cached.Setup(e => e.GetCachedPropertyValue(UiaIds.IsPasswordProperty)).Returns(true);
        var (node, _, _) = CachedBy(sut, cached);

        // Act
        var identity = sut.ReadIdentity(node);

        // Assert
        identity.Should().Be(new UiaNodeIdentity("PasswordBox", "WPF", 4242, 0x1234, true));
    }

    [Fact]
    public void ReadIdentity_PropertiesTheProviderDoesNotSupport_ReadAsNoProcessNoWindowAndNoPassword()
    {
        // Arrange
        var sut = CreateSut();
        var cached = new Mock<IUIAutomationElement>();
        cached.Setup(e => e.GetCachedPropertyValue(It.IsAny<int>())).Returns(NotSupported);
        var (node, _, _) = CachedBy(sut, cached);

        // Act
        var identity = sut.ReadIdentity(node);

        // Assert - process 0 is no process: the action tools' target checks refuse it
        identity.Should().Be(new UiaNodeIdentity(null, null, 0, 0, false));
    }

    [Fact]
    public void ReadInteraction_ReadsFocusAndTheActionPatterns_InOneCall()
    {
        // Arrange - LegacyIAccessible and Scroll are available too, but they are not action patterns
        var sut = CreateSut();
        var cached = Cached(null, 50000, focusable: true, availablePatternProperties:
        [
            UiaIds.IsInvokePatternAvailableProperty, UiaIds.IsScrollPatternAvailableProperty,
            UiaIds.IsLegacyIAccessiblePatternAvailableProperty
        ]);
        cached.Setup(e => e.GetCachedPropertyValue(UiaIds.HasKeyboardFocusProperty)).Returns(true);
        var (node, element, requests) = CachedBy(sut, cached);

        // Act
        var interaction = sut.ReadInteraction(node);

        // Assert
        interaction.IsKeyboardFocusable.Should().BeTrue();
        interaction.HasKeyboardFocus.Should().BeTrue();
        interaction.ActionPatterns.Should().Equal("Invoke");
        var request = _requests.Single(r => r.Object == requests.Single());
        request.Verify(r => r.AddProperty(UiaIds.IsKeyboardFocusableProperty), Times.Once);
        request.Verify(r => r.AddProperty(UiaIds.HasKeyboardFocusProperty), Times.Once);
        foreach (var (property, _) in UiaIds.PatternAvailability)
        {
            request.Verify(r => r.AddProperty(property), Times.Once, $"property {property}");
        }

        element.Verify(e => e.GetCurrentPropertyValue(It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData(false, UiaIds.IsLegacyIAccessiblePatternAvailableProperty, false)]
    [InlineData(false, UiaIds.IsScrollPatternAvailableProperty, false)]
    [InlineData(false, UiaIds.IsTogglePatternAvailableProperty, true)]
    [InlineData(true, UiaIds.IsScrollPatternAvailableProperty, true)]
    public void IsInteractable_IsFocusabilityOrAnActionPattern_NotLegacyIAccessible(bool focusable, int availablePattern, bool expected)
    {
        // Arrange - the UIA3 engine reports LegacyIAccessible on nearly every element, so it makes nothing interactable
        var sut = CreateSut();
        var (node, _, _) = CachedBy(sut, Cached(null, 50020, focusable: focusable, availablePatternProperties: availablePattern));

        // Act & Assert
        sut.IsInteractable(node).Should().Be(expected);
    }

    #endregion

    #region Search conditions

    [Fact]
    public void FindAll_TranslatesTheCondition_ComparingControlTypesByIdAndNamesIgnoringCase()
    {
        // Arrange
        var sut = CreateSut();
        var window = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, window);
        var isButton = new Mock<IUIAutomationCondition>().Object;
        var namedSave = new Mock<IUIAutomationCondition>().Object;
        var both = new Mock<IUIAutomationCondition>().Object;
        var search = new Mock<IUIAutomationCondition>().Object;
        _automation.Setup(a => a.CreatePropertyCondition(UiaIds.ControlTypeProperty, 50000)).Returns(isButton);
        _automation.Setup(a => a.CreatePropertyConditionEx(UiaIds.NameProperty, "save", PropertyConditionFlags.PropertyConditionFlags_IgnoreCase))
            .Returns(namedSave);
        _automation.Setup(a => a.CreateAndCondition(isButton, namedSave)).Returns(both);
        _automation.Setup(a => a.CreateAndCondition(_controlView.Object, both)).Returns(search);
        var found = ArrayOf(Cached("Save", 50000));
        window.Setup(e => e.FindAllBuildCache(TreeScope.TreeScope_Descendants, search, It.IsAny<IUIAutomationCacheRequest>()))
            .Returns(found.Object);

        // Act
        var result = sut.FindAll(node, UiaScope.Descendants,
            UiaCondition.And(
                UiaCondition.PropertyEquals(UiaProperty.ControlType, "button"),
                UiaCondition.PropertyEquals(UiaProperty.Name, "save", ignoreCase: true)),
            CancellationToken.None);

        // Assert - one call in the provider's process
        result.Select(e => e.Properties.Name).Should().Equal("Save");
    }

    [Fact]
    public void FindAll_OfTheNewerControlTypes_ComparesTheirIds()
    {
        // The managed client cannot express these at all (UiaTree throws NotSupportedException)
        var sut = CreateSut();
        var window = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, window);
        window.Setup(e => e.FindAllBuildCache(It.IsAny<TreeScope>(), It.IsAny<IUIAutomationCondition>(), It.IsAny<IUIAutomationCacheRequest>()))
            .Returns(ArrayOf().Object);

        sut.FindAll(node, UiaScope.Children, UiaCondition.PropertyEquals(UiaProperty.ControlType, "AppBar"), CancellationToken.None);

        _automation.Verify(a => a.CreatePropertyCondition(UiaIds.ControlTypeProperty, 50040), Times.Once);
    }

    [Fact]
    public void FindAll_ByRuntimeId_ComparesTheIdAsAnIntArray()
    {
        // Arrange - the element registry finds an element again by its runtime id (roadmap M3.4)
        var sut = CreateSut();
        var window = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, window);
        window.Setup(e => e.FindAllBuildCache(It.IsAny<TreeScope>(), It.IsAny<IUIAutomationCondition>(), It.IsAny<IUIAutomationCacheRequest>()))
            .Returns(ArrayOf().Object);

        // Act
        sut.FindAll(node, UiaScope.Descendants, UiaCondition.PropertyEquals(UiaProperty.RuntimeId, new List<int> { 42, 7 }), CancellationToken.None);

        // Assert
        _automation.Verify(a => a.CreatePropertyCondition(
            UiaIds.RuntimeIdProperty, It.Is<object>(o => o is int[] && ((int[])o).SequenceEqual(new[] { 42, 7 }))), Times.Once);
    }

    [Fact]
    public void Conditions_OnARuntimeId_TakeACopyOfANonEmptyList()
    {
        // Arrange
        var id = new List<int> { 42, 7 };

        // Act
        var condition = (UiaPropertyCondition)UiaCondition.PropertyEquals(UiaProperty.RuntimeId, id);
        id[1] = 8;

        // Assert
        ((int[])condition.Value).Should().Equal(42, 7);
        FluentActions.Invoking(() => UiaCondition.PropertyEquals(UiaProperty.RuntimeId, new int[0])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => UiaCondition.PropertyEquals(UiaProperty.RuntimeId, "42.7")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Conditions_RefuseValuesOfTheWrongType()
    {
        FluentActions.Invoking(() => UiaCondition.PropertyEquals(UiaProperty.IsEnabled, "yes")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => UiaCondition.PropertyEquals(UiaProperty.ProcessId, 5, ignoreCase: true)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => UiaCondition.And()).Should().Throw<ArgumentException>();
    }

    #endregion

    #region Patterns

    [Fact]
    public void GetPattern_WrapsTheElementsPattern_AndItsActionReachesIt()
    {
        // Arrange
        var sut = CreateSut();
        var button = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, button);
        var invoke = new Mock<IUIAutomationInvokePattern>();
        button.Setup(e => e.GetCurrentPattern(UiaIds.InvokePattern)).Returns(invoke.Object);

        // Act
        sut.GetPattern<IUiaInvokePattern>(node)!.Invoke();

        // Assert
        invoke.Verify(p => p.Invoke(), Times.Once);
    }

    [Fact]
    public void GetPattern_OfAPatternTheElementLacks_IsNull()
    {
        var sut = CreateSut();
        var text = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, text);

        sut.GetPattern<IUiaTogglePattern>(node).Should().BeNull();
    }

    [Fact]
    public void LegacyIAccessible_RunsTheDefaultAction()
    {
        // Arrange - an MSAA-only control
        var sut = CreateSut();
        var control = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, control);
        var legacy = new Mock<IUIAutomationLegacyIAccessiblePattern>();
        legacy.SetupGet(p => p.CurrentDefaultAction).Returns("Press");
        control.Setup(e => e.GetCurrentPattern(UiaIds.LegacyIAccessiblePattern)).Returns(legacy.Object);

        // Act
        var pattern = sut.GetPattern<IUiaLegacyIAccessiblePattern>(node)!;
        pattern.DoDefaultAction();

        // Assert
        pattern.DefaultAction.Should().Be("Press");
        legacy.Verify(p => p.DoDefaultAction(), Times.Once);
    }

    [Fact]
    public void LegacyIAccessible_ReadsOnlyTheDefaultAction_NeverTheValue()
    {
        // Arrange - MSAA's value of a password field can be the password
        var sut = CreateSut();
        var control = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, control);
        var legacy = new Mock<IUIAutomationLegacyIAccessiblePattern>(MockBehavior.Strict);
        legacy.SetupGet(p => p.CurrentDefaultAction).Returns("Press");
        control.Setup(e => e.GetCurrentPattern(UiaIds.LegacyIAccessiblePattern)).Returns(legacy.Object);

        // Act
        var defaultAction = sut.GetPattern<IUiaLegacyIAccessiblePattern>(node)!.DefaultAction;

        // Assert - a strict mock: a read of CurrentValue (or any other member) would throw
        defaultAction.Should().Be("Press");
        legacy.VerifyGet(p => p.CurrentDefaultAction, Times.Once);
    }

    [Fact]
    public void PatternActions_OnADisabledElement_ThrowInvalidOperation()
    {
        // Arrange
        var sut = CreateSut();
        var box = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, box);
        var toggle = new Mock<IUIAutomationTogglePattern>();
        toggle.Setup(p => p.Toggle()).Throws(new COMException("not enabled", UiaComErrors.ElementNotEnabled));
        toggle.SetupGet(p => p.CurrentToggleState).Returns(ToggleState.ToggleState_On);
        box.Setup(e => e.GetCurrentPattern(UiaIds.TogglePattern)).Returns(toggle.Object);
        var pattern = sut.GetPattern<IUiaTogglePattern>(node)!;

        // Act & Assert
        pattern.ToggleState.Should().Be(UiaToggleState.On);
        pattern.Invoking(p => p.Toggle()).Should().Throw<InvalidOperationException>().WithMessage("*not enabled*");
    }

    [Fact]
    public void ReadIsModal_ReadsTheWindowPattern()
    {
        // Arrange
        var sut = CreateSut();
        var dialog = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, dialog);
        var window = new Mock<IUIAutomationWindowPattern>();
        window.SetupGet(p => p.CurrentIsModal).Returns(1);
        dialog.Setup(e => e.GetCurrentPattern(UiaIds.WindowPattern)).Returns(window.Object);

        // Act & Assert
        sut.ReadIsModal(node).Should().BeTrue();
    }

    [Fact]
    public void ReadIsModal_ReadsOnlyIsModal()
    {
        // Arrange - a provider that fails every other Window-pattern property
        var sut = CreateSut();
        var dialog = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, dialog);
        var window = new Mock<IUIAutomationWindowPattern>(MockBehavior.Strict);
        window.SetupGet(p => p.CurrentIsModal).Returns(0);
        dialog.Setup(e => e.GetCurrentPattern(UiaIds.WindowPattern)).Returns(window.Object);

        // Act & Assert - one cross-process read, as with the managed client; a strict mock throws for any other
        sut.ReadIsModal(node).Should().BeFalse();
        window.VerifyGet(p => p.CurrentIsModal, Times.Once);
    }

    [Fact]
    public void ReadIsModal_OfAnElementWithoutAWindowPattern_IsNull()
    {
        var sut = CreateSut();
        var pane = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, pane);

        sut.ReadIsModal(node).Should().BeNull();
    }

    [Fact]
    public void GetPattern_WrapsByTheRequestedType_WhenThePatternObjectAlsoAnswersForAnotherPattern()
    {
        // Arrange - a table pattern object that is also a grid pattern (QueryInterface answers for both)
        var sut = CreateSut();
        var grid = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, grid);
        var table = new Mock<IUIAutomationTablePattern>();
        table.SetupGet(p => p.CurrentRowOrColumnMajor).Returns(RowOrColumnMajor.RowOrColumnMajor_ColumnMajor);
        table.As<IUIAutomationGridPattern>().SetupGet(p => p.CurrentRowCount).Returns(7);
        grid.Setup(e => e.GetCurrentPattern(UiaIds.TablePattern)).Returns(table.Object);
        grid.Setup(e => e.GetCurrentPattern(UiaIds.GridPattern)).Returns(table.Object);

        // Act & Assert
        sut.GetPattern<IUiaTablePattern>(node)!.RowOrColumnMajor.Should().Be(UiaRowOrColumnMajor.ColumnMajor);
        sut.GetPattern<IUiaGridPattern>(node)!.RowCount.Should().Be(7);
    }

    [Fact]
    public void GetTextPage_MovesTheEndpointsOfItsOwnRange_AndReadsOnlyThePage()
    {
        // Arrange - a strict range: anything but these calls (Select, a selection, the whole text) fails the test
        var sut = CreateSut();
        var document = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, document);
        var range = new Mock<IUIAutomationTextRange>(MockBehavior.Strict);
        var probe = new Mock<IUIAutomationTextRange>(MockBehavior.Strict);
        range.Setup(r => r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, 6)).Returns(6);
        range.Setup(r => r.MoveEndpointByRange(
            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, range.Object, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start));
        range.Setup(r => r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, 3)).Returns(3);
        range.Setup(r => r.GetText(6)).Returns("wor");
        range.Setup(r => r.Clone()).Returns(probe.Object);
        probe.Setup(r => r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, 1)).Returns(1);
        var text = new Mock<IUIAutomationTextPattern>();
        text.SetupGet(p => p.DocumentRange).Returns(range.Object);
        document.Setup(e => e.GetCurrentPattern(UiaIds.TextPattern)).Returns(text.Object);

        // Act
        var page = sut.GetPattern<IUiaTextPattern>(node)!.GetTextPage(6, 3);

        // Assert
        page.Should().Be(new UiaTextPage("wor", 6, 3, HasMore: true));
    }

    [Fact]
    public void GetTextPage_AtTheEnd_NeedsNoProbe_AndKnowsTheLength()
    {
        // Arrange - a page shorter than asked for: the document ends in it
        var sut = CreateSut();
        var document = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, document);
        var range = new Mock<IUIAutomationTextRange>(MockBehavior.Strict);
        range.Setup(r => r.MoveEndpointByRange(
            TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, range.Object, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start));
        range.Setup(r => r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, 100)).Returns(11);
        range.Setup(r => r.GetText(200)).Returns("hello world");
        var text = new Mock<IUIAutomationTextPattern>();
        text.SetupGet(p => p.DocumentRange).Returns(range.Object);
        document.Setup(e => e.GetCurrentPattern(UiaIds.TextPattern)).Returns(text.Object);

        // Act
        var page = sut.GetPattern<IUiaTextPattern>(node)!.GetTextPage(0, 100);

        // Assert
        page.Should().Be(new UiaTextPage("hello world", 0, 11, HasMore: false));
    }

    [Fact]
    public void GetTextPage_WhenTheProvidersCharactersAreWider_ReadsAShorterRange_NeverTheWholeText()
    {
        // Arrange - a strict range whose 100 characters hold 1,000 UTF-16 characters: GetText(-1) would fail the test
        var sut = CreateSut();
        var document = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, document);
        var range = new Mock<IUIAutomationTextRange>(MockBehavior.Strict);
        var spanned = 0;
        range.Setup(r => r.MoveEndpointByRange(
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, range.Object, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start))
            .Callback(() => spanned = 0);
        range.Setup(r => r.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, It.IsAny<int>()))
            .Returns((TextPatternRangeEndpoint _, TextUnit _, int count) => spanned = Math.Min(count, 100));
        range.Setup(r => r.GetText(It.IsInRange(1, 200, Moq.Range.Inclusive)))
            .Returns((int maxLength) => new string('x', Math.Min(spanned * 10, maxLength)));
        var text = new Mock<IUIAutomationTextPattern>();
        text.SetupGet(p => p.DocumentRange).Returns(range.Object);
        document.Setup(e => e.GetCurrentPattern(UiaIds.TextPattern)).Returns(text.Object);

        // Act
        var page = sut.GetPattern<IUiaTextPattern>(node)!.GetTextPage(0, 100);

        // Assert - the page spans the characters whose text fits, so the next page starts just after it
        page.Text.Length.Should().BeLessThanOrEqualTo(100);
        page.Text.Length.Should().Be(page.Length * 10);
        page.HasMore.Should().BeTrue();
        page.Cut.Should().BeFalse();
    }

    [Fact]
    public void GetPattern_WhenThePatternObjectIsNotOfTheRequestedPattern_IsNull()
    {
        // Arrange - a provider that answers the pattern id with an object of another pattern
        var sut = CreateSut();
        var button = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, button);
        button.Setup(e => e.GetCurrentPattern(UiaIds.TogglePattern)).Returns(new Mock<IUIAutomationInvokePattern>().Object);

        // Act & Assert
        sut.GetPattern<IUiaTogglePattern>(node).Should().BeNull();
    }

    #endregion

    #region Acting (roadmap M3.6)

    [Fact]
    public void FirstUse_TurnsOffAutoSetFocus_SoPatternActionsDoNotMoveFocusOrTheForeground()
    {
        // Arrange - a Windows 10 1809+ client (IUIAutomation6), where AutoSetFocus is on by default
        var six = _automation.As<IUIAutomation6>();
        six.SetupProperty(a => a.AutoSetFocus, 1);
        var sut = CreateSut();

        // Act
        sut.IsAvailable().Should().BeTrue();

        // Assert
        six.Object.AutoSetFocus.Should().Be(0);
    }

    [Fact]
    public void FirstUse_OfAClientWithoutIUIAutomation6_StillStarts()
    {
        CreateSut().IsAvailable().Should().BeTrue();
    }

    [Fact]
    public void SetFocus_ReachesTheElement()
    {
        // Arrange
        var sut = CreateSut();
        var box = new Mock<IUIAutomationElement>();
        var node = NodeOf(sut, box);

        // Act
        sut.SetFocus(node);

        // Assert
        box.Verify(e => e.SetFocus(), Times.Once);
    }

    [Fact]
    public void SetFocus_OfAnElementThatCannotTakeFocus_ThrowsInvalidOperation()
    {
        // Arrange
        var sut = CreateSut();
        var label = new Mock<IUIAutomationElement>();
        label.Setup(e => e.SetFocus()).Throws(new COMException("not enabled", UiaComErrors.ElementNotEnabled));
        var node = NodeOf(sut, label);

        // Act & Assert
        sut.Invoking(t => t.SetFocus(node)).Should().Throw<InvalidOperationException>();
    }

    #endregion
}
