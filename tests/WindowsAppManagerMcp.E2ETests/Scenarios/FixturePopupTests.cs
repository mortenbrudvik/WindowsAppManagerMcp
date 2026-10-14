namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Popup-aware element search (roadmap M3.5) against the fixture's WPF context menu and combo box: once open, each is a
/// window of its own (a WPF Popup), outside the main window's element tree. find_ui_elements' default scope finds their
/// items while the fixture is in front, registered under the popup's window, and get_active_popups lists the popup with a
/// ref. Input tier: a right click opens the menu and a click opens the dropdown; Esc closes them.
/// </summary>
/// <remarks>
/// Not covered here, for want of a fixture surface: a Win32 #32768 menu and a ComboLBox (the fixture is WPF), a WinUI
/// flyout and a WebView2 popup. The unit tests cover their classification and the child-process rule with fakes.
/// </remarks>
public class FixturePopupTests
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);

    private readonly ITestOutputHelper _output;

    public FixturePopupTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static ElementQuery Query(nint window, string? name = null, string? automationId = null, string[]? controlTypes = null,
        ElementSearchScope? scope = null) =>
        new(window, Name: name, AutomationId: automationId, ControlTypes: controlTypes, Timeout: TimeSpan.FromSeconds(10), Scope: scope);

    [DesktopFact]
    public async Task ContextMenu_ItsItemsAreFoundByTheDefaultScope_UnderThePopupsOwnWindow()
    {
        // Arrange - right-click the menu target in the fixture, in front
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var target = e2e.Fixture.Ready.Element(FixtureContract.Ids.ContextMenuTarget);
        e2e.Server.Foreground.EnsureForeground(main).IsForeground.Should().BeTrue();
        var popups = e2e.Server.Get<IPopupService>();
        var before = popups.GetPopups(main).Select(p => p.Handle).ToHashSet();

        var click = e2e.Server.Input.RightClick(target.CenterX, target.CenterY, InputTarget.Window(main), releaseHeldModifiers: false, CancellationToken.None);
        click.Success.Should().BeTrue(click.Error);
        e2e.Fixture.WaitForEvent(FixtureContract.Events.ContextMenuOpened);
        var opened = await popups.WaitForNewPopupAsync(main, before, SettleTimeout, CancellationToken.None);
        _output.WriteLine($"opened popup: {opened}");

        try
        {
            // Act
            var found = await e2e.Server.Get<IElementSearchService>().FindAsync(Query(main, automationId: FixtureContract.Ids.ContextMenuDelete));
            var listed = await popups.GetActivePopupsAsync(main, TimeSpan.FromSeconds(10), CancellationToken.None);

            // Assert
            opened.Should().NotBeNull("the context menu is a window of its own");
            found.Success.Should().BeTrue(found.Error);
            found.Scope.Should().Be(ElementSearchScope.WindowAndPopups, "the fixture is in front");
            var delete = found.Elements.Should().ContainSingle().Which;
            _output.WriteLine($"{delete.Ref} {delete.ControlType} '{delete.Name}' in window {delete.WindowHandle}");
            delete.ControlType.Should().Be("MenuItem");
            delete.WindowHandle.Should().Be((long)opened!.Handle);
            e2e.Server.Get<IElementRegistry>().Lookup(delete.Ref!).WindowHandle.Should().Be(opened.Handle);

            var menu = listed.Popups.Should().Contain(p => p.Handle == (long)opened.Handle).Which;
            _output.WriteLine($"get_active_popups: {menu.Kind} {menu.ClassName} {menu.ControlType} {menu.Ref}");
            menu.Ref.Should().NotBeNull();
            menu.Kind.Should().BeOneOf(PopupKind.Menu, PopupKind.Popup);

            var windowOnly = await e2e.Server.Get<IElementSearchService>().FindAsync(
                Query(main, automationId: FixtureContract.Ids.ContextMenuDelete, scope: ElementSearchScope.Window));
            windowOnly.MatchCount.Should().Be(0, "the menu is not in the main window's tree");
        }
        finally
        {
            await e2e.Server.Input.SendKeysAsync("{ESC}", InputTarget.Window(main), releaseHeldModifiers: false, 0, CancellationToken.None);
        }
    }

    [DesktopFact]
    public async Task ComboBoxDropdown_ItsItemsAreFoundByScopeWindowAndPopups()
    {
        // Arrange - click the combo box open
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var combo = e2e.Fixture.Ready.Element(FixtureContract.Ids.ComboBox);
        var popups = e2e.Server.Get<IPopupService>();
        var before = popups.GetPopups(main).Select(p => p.Handle).ToHashSet();

        var click = e2e.Server.Input.Click(combo.CenterX, combo.CenterY, InputTarget.Window(main), releaseHeldModifiers: false, CancellationToken.None);
        click.Success.Should().BeTrue(click.Error);
        var opened = await popups.WaitForNewPopupAsync(main, before, SettleTimeout, CancellationToken.None);
        _output.WriteLine($"opened popup: {opened}");

        try
        {
            // Act
            var found = await e2e.Server.Get<IElementSearchService>().FindAsync(
                Query(main, name: FixtureContract.ComboBoxItems[2], controlTypes: ["ListItem"], scope: ElementSearchScope.WindowAndPopups));

            // Assert - WPF may expose the open list's items under the ComboBox of the main window as well as under the
            // popup's window: the scope must find the visible item, wherever the provider puts it
            opened.Should().NotBeNull("the dropdown is a window of its own");
            found.Success.Should().BeTrue(found.Error);
            found.WindowsSearched.Should().BeGreaterThan(1, "the open dropdown is searched too");
            foreach (var element in found.Elements!)
            {
                _output.WriteLine($"{element.Ref} {element.ControlType} '{element.Name}' in window {element.WindowHandle} click {element.ClickPoint}");
            }

            found.Elements.Should().Contain(e => e.Name == FixtureContract.ComboBoxItems[2] && e.ClickPoint != null);
            found.Elements.Where(e => e.WindowHandle != null).Should().OnlyContain(e => e.WindowHandle == (long)opened!.Handle);
        }
        finally
        {
            await e2e.Server.Input.SendKeysAsync("{ESC}", InputTarget.Window(main), releaseHeldModifiers: false, 0, CancellationToken.None);
        }
    }
}
