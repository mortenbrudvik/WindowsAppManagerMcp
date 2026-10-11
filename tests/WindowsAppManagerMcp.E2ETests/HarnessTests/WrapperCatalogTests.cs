using System.Reflection;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The guard's explicit classification of the server's native wrapper members (<see cref="WrapperCatalog"/>).
/// A wrapper interface or member that a later change adds fails here until someone classifies it, so the
/// guard never decides from a member's name. Reflection only; nothing is called.
/// </summary>
public class WrapperCatalogTests
{
    [Fact]
    public void TheServersNativeWrapperInterfaces_AreFound()
    {
        // Guards the tests below against passing on an empty discovery
        WrapperGuard.ServerNativeWrapperInterfaces.Should().Contain(new[]
        {
            typeof(INativeWindowWrapper),
            typeof(INativeInputWrapper),
            typeof(IUIAutomationWrapper),
            typeof(IScreenCaptureWrapper),
            typeof(INativeMonitorWrapper),
            typeof(INativeProcessWrapper),
            typeof(IProcessHandle),
            typeof(ITcpPortOwnerResolver),
            typeof(INativeHitTestWrapper),
            typeof(INativeSecurityWrapper),
            typeof(INativeSystemInfoWrapper),
            typeof(INativeThreadInputWrapper),
            typeof(INativeHotkeyWrapper),
            typeof(IDesktopPauseFlag),
            typeof(IUiaTree),
            // The pattern objects the engine adapters hand out (roadmap M3.2)
            typeof(IUiaInvokePattern),
            typeof(IUiaLegacyIAccessiblePattern)
        });
    }

    [Fact]
    public void OpaqueNodeHandles_AreNotTreatedAsWrappers()
    {
        // IUiaNode has nothing to call; IUiaTree takes it back, so it must reach the tree unproxied
        WrapperGuard.IsOpaqueToken(typeof(IUiaNode)).Should().BeTrue();
        WrapperGuard.ServerNativeWrapperInterfaces.Should().NotContain(typeof(IUiaNode));
        WrapperGuard.IsOpaqueToken(typeof(IUiaTree)).Should().BeFalse();
    }

    [Fact]
    public void EveryNativeWrapperInterfaceOfTheServer_IsInTheCatalog()
    {
        var missing = WrapperGuard.ServerNativeWrapperInterfaces.Except(WrapperCatalog.Interfaces).Select(t => t.Name);

        missing.Should().BeEmpty($"every native wrapper interface must be classified in {nameof(WrapperCatalog)}");
    }

    [Fact]
    public void TheCatalog_ListsOnlyNativeWrapperInterfacesOfTheServer()
    {
        WrapperCatalog.Interfaces.Should().OnlyContain(t => WrapperGuard.IsNativeWrapper(t));
    }

    [Fact]
    public void EveryMemberOfEveryNativeWrapper_IsClassified()
    {
        var missing = WrapperGuard.ServerNativeWrapperInterfaces
            .SelectMany(type => AllMethods(type)
                .Where(method => WrapperCatalog.Find(type, method) == null)
                .Select(method => $"{type.Name}.{method.Name}"));

        missing.Should().BeEmpty(
            $"the guard refuses unclassified members; classify each new member in {nameof(WrapperCatalog)} " +
            "(Read only if it changes nothing on the desktop or in any process)");
    }

    [Fact]
    public void TheCatalog_NamesOnlyMembersThatExist()
    {
        var stale = WrapperCatalog.Interfaces
            .SelectMany(type => WrapperCatalog.MembersOf(type).Keys
                .Where(name => AllMethods(type).All(method => method.Name != name))
                .Select(name => $"{type.Name}.{name}"));

        stale.Should().BeEmpty();
    }

    [Fact]
    public void WrapperMemberNames_AreUnique()
    {
        // The catalog classifies by name, so an overload would silently share its sibling's classification
        var overloaded = WrapperGuard.ServerNativeWrapperInterfaces
            .SelectMany(type => AllMethods(type)
                .GroupBy(method => method.Name)
                .Where(group => group.Count() > 1)
                .Select(group => $"{type.Name}.{group.Key}"));

        overloaded.Should().BeEmpty();
    }

    [Fact]
    public void WindowActions_AccountForEveryHandleParameter()
    {
        var problems = new List<string>();
        foreach (var type in WrapperCatalog.Interfaces)
        {
            foreach (var method in AllMethods(type))
            {
                if (WrapperCatalog.Find(type, method) is not { } member)
                {
                    continue;
                }

                var handles = method.GetParameters().Where(p => p.ParameterType == typeof(nint)).Select(p => p.Name!).ToHashSet();
                var listed = member.WindowParameters.Concat(member.OtherHandleParameters).ToHashSet();
                if (member.Kind != WrapperCallKind.WindowAction)
                {
                    if (listed.Count > 0)
                    {
                        problems.Add($"{type.Name}.{method.Name} is {member.Kind} but lists handle parameters");
                    }
                }
                else if (member.WindowParameters.Count == 0 || !handles.SetEquals(listed))
                {
                    problems.Add($"{type.Name}.{method.Name}: handle parameters [{string.Join(", ", handles)}], " +
                                 $"listed [{string.Join(", ", listed)}]");
                }
            }
        }

        problems.Should().BeEmpty("every window handle of a window action must be checked against the fixture");
    }

    [Fact]
    public void WrappersAreHandedOutOnlyAsReturnValues()
    {
        // The guard wraps a wrapper a member returns (a process handle). It does not look inside out
        // parameters, tasks, arrays or collections, so a member that hands one out that way would leak an
        // unguarded wrapper.
        var leaks = new List<string>();
        foreach (var type in WrapperGuard.ServerNativeWrapperInterfaces)
        {
            foreach (var method in AllMethods(type))
            {
                if (method.ReturnType != typeof(void) && !WrapperGuard.IsNativeWrapper(method.ReturnType) && MentionsWrapper(method.ReturnType))
                {
                    leaks.Add($"{type.Name}.{method.Name} returns {method.ReturnType.Name}");
                }
                leaks.AddRange(method.GetParameters()
                    .Where(p => p.ParameterType.IsByRef && MentionsWrapper(p.ParameterType.GetElementType()!))
                    .Select(p => $"{type.Name}.{method.Name} hands out a wrapper through '{p.Name}'"));
            }
        }

        leaks.Should().BeEmpty();
    }

    [Theory]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.EnumWindows), WrapperCallKind.Read)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.GetForegroundWindow), WrapperCallKind.Read)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.SetForegroundWindow), WrapperCallKind.WindowAction)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.BringWindowToTop), WrapperCallKind.WindowAction)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.SetWindowPos), WrapperCallKind.WindowAction)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.ShowWindow), WrapperCallKind.WindowAction)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.PostMessage), WrapperCallKind.WindowAction)]
    [InlineData(typeof(INativeInputWrapper), nameof(INativeInputWrapper.GetCursorPos), WrapperCallKind.Read)]
    [InlineData(typeof(INativeInputWrapper), nameof(INativeInputWrapper.SendInput), WrapperCallKind.Input)]
    [InlineData(typeof(INativeInputWrapper), nameof(INativeInputWrapper.SetCursorPos), WrapperCallKind.Input)]
    [InlineData(typeof(INativeInputWrapper), nameof(INativeInputWrapper.IsKeyDown), WrapperCallKind.Read)]
    [InlineData(typeof(INativeHotkeyWrapper), nameof(INativeHotkeyWrapper.RegisterHotKey), WrapperCallKind.GlobalHotkey)]
    [InlineData(typeof(INativeHotkeyWrapper), nameof(INativeHotkeyWrapper.WaitForMessage), WrapperCallKind.GlobalHotkey)]
    [InlineData(typeof(INativeHotkeyWrapper), nameof(INativeHotkeyWrapper.OpenDesktopPause), WrapperCallKind.GlobalHotkey)]
    [InlineData(typeof(INativeHotkeyWrapper), nameof(INativeHotkeyWrapper.MarkChordHolder), WrapperCallKind.GlobalHotkey)]
    [InlineData(typeof(IDesktopPauseFlag), nameof(IDesktopPauseFlag.Set), WrapperCallKind.GlobalHotkey)]
    [InlineData(typeof(IDesktopPauseFlag), "get_" + nameof(IDesktopPauseFlag.IsSet), WrapperCallKind.Read)]
    [InlineData(typeof(IUIAutomationWrapper), nameof(IUIAutomationWrapper.TryGetElementFromHandle), WrapperCallKind.Read)]
    [InlineData(typeof(IScreenCaptureWrapper), nameof(IScreenCaptureWrapper.CaptureWindow), WrapperCallKind.Read)]
    [InlineData(typeof(INativeMonitorWrapper), nameof(INativeMonitorWrapper.MonitorFromWindow), WrapperCallKind.Read)]
    [InlineData(typeof(INativeMonitorWrapper), nameof(INativeMonitorWrapper.MonitorFromPoint), WrapperCallKind.Read)]
    [InlineData(typeof(ITcpPortOwnerResolver), nameof(ITcpPortOwnerResolver.GetListeningProcessId), WrapperCallKind.Read)]
    [InlineData(typeof(INativeProcessWrapper), nameof(INativeProcessWrapper.OpenProcess), WrapperCallKind.Read)]
    [InlineData(typeof(INativeProcessWrapper), nameof(INativeProcessWrapper.StartProcess), WrapperCallKind.ProcessStart)]
    [InlineData(typeof(INativeProcessWrapper), nameof(INativeProcessWrapper.FindWithWhereExe), WrapperCallKind.ProcessStart)]
    [InlineData(typeof(IProcessHandle), nameof(IProcessHandle.CloseMainWindow), WrapperCallKind.ProcessAction)]
    [InlineData(typeof(IProcessHandle), nameof(IProcessHandle.Kill), WrapperCallKind.ProcessAction)]
    [InlineData(typeof(IProcessHandle), nameof(IDisposable.Dispose), WrapperCallKind.Read)]
    [InlineData(typeof(INativeThreadInputWrapper), nameof(INativeThreadInputWrapper.GetGuiThreadInfo), WrapperCallKind.Read)]
    [InlineData(typeof(INativeThreadInputWrapper), nameof(INativeThreadInputWrapper.AttachThreadInput), WrapperCallKind.ThreadInputAttach)]
    [InlineData(typeof(INativeClipboardWrapper), nameof(INativeClipboardWrapper.GetSequenceNumber), WrapperCallKind.Read)]
    [InlineData(typeof(INativeClipboardWrapper), nameof(INativeClipboardWrapper.GetOwnerProcessId), WrapperCallKind.Read)]
    [InlineData(typeof(INativeClipboardWrapper), nameof(INativeClipboardWrapper.Read), WrapperCallKind.Clipboard)]
    [InlineData(typeof(INativeClipboardWrapper), nameof(INativeClipboardWrapper.WriteText), WrapperCallKind.Clipboard)]
    [InlineData(typeof(INativeHitTestWrapper), nameof(INativeHitTestWrapper.GetClientAreaOnScreen), WrapperCallKind.Read)]
    [InlineData(typeof(INativeSystemInfoWrapper), nameof(INativeSystemInfoWrapper.GetMouseWheelRouting), WrapperCallKind.Read)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.RespondsWithin), WrapperCallKind.Read)]
    [InlineData(typeof(IUiaTree), nameof(IUiaTree.GetChildElements), WrapperCallKind.Read)]
    [InlineData(typeof(IUiaTree), nameof(IUiaTree.GetPattern), WrapperCallKind.Read)]
    [InlineData(typeof(IUiaInvokePattern), nameof(IUiaInvokePattern.Invoke), WrapperCallKind.UiaAction)]
    [InlineData(typeof(IUiaValuePattern), nameof(IUiaValuePattern.SetValue), WrapperCallKind.UiaAction)]
    [InlineData(typeof(IUiaWindowPattern), nameof(IUiaWindowPattern.Close), WrapperCallKind.UiaAction)]
    [InlineData(typeof(IUiaLegacyIAccessiblePattern), nameof(IUiaLegacyIAccessiblePattern.DoDefaultAction), WrapperCallKind.UiaAction)]
    [InlineData(typeof(IUiaTextPattern), nameof(IUiaTextPattern.GetText), WrapperCallKind.Read)]
    public void Find_ClassifiesTheMembersTheScenariosAndLaterWavesDependOn(Type type, string name, WrapperCallKind expected)
    {
        var method = AllMethods(type).Single(m => m.Name == name);

        WrapperCatalog.Find(type, method)!.Kind.Should().Be(expected);
    }

    [Fact]
    public void Find_ReturnsNullForAnInterfaceTheCatalogDoesNotKnow()
    {
        var method = typeof(IWindowService).GetMethods().First();

        WrapperCatalog.Find(typeof(IWindowService), method).Should().BeNull();
    }

    /// <summary>The members of <paramref name="type"/>, including those of the interfaces it extends (IDisposable).</summary>
    private static IReadOnlyList<MethodInfo> AllMethods(Type type) =>
        type.GetMethods().Concat(type.GetInterfaces().SelectMany(i => i.GetMethods())).ToList();

    private static bool MentionsWrapper(Type type) =>
        WrapperGuard.IsNativeWrapper(type) ||
        (type.HasElementType && MentionsWrapper(type.GetElementType()!)) ||
        (type.IsGenericType && type.GetGenericArguments().Any(MentionsWrapper));
}
