namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// The "Open File - Security Warning" prompt (roadmap M3.F5) on a mocked <see cref="INativeWindowWrapper"/>: a dialog box
/// whose child controls carry the IDs and classes of shdocvw.dll's dialog template 4416, which the target policy recognizes
/// (<see cref="SecurityPrompts"/>). Nothing touches the real desktop.
/// </summary>
public static class SecurityPromptWindows
{
    // Win32 value (NativeEnums is internal to the server assembly)
    public const int GwlpId = -12;

    /// <summary>
    /// Every control of the template, in its order: the ones the policy looks at and the labels, the hidden Save button,
    /// Cancel, the separator and the shield icon (IDC_STATIC is 65535).
    /// </summary>
    public static IReadOnlyList<(int Id, string ClassName)> Controls { get; } =
    [
        (4417, "Static"), (4418, "Static"), (65535, "Static"), (4419, "SysLink"), (4420, "Static"), (4421, "SysLink"),
        (4422, "Static"), (4423, "Static"), (4424, "Static"), (4425, "Static"), (4426, "Button"), (4427, "Button"),
        (2, "Button"), (4428, "Button"), (4429, "Static"), (4430, "Static"), (4431, "SysLink")
    ];

    /// <summary>
    /// The controls of Windows' "Windows Security Warning" (template 4437, a blocked file with only OK), which reuses
    /// IDs 4417, 4419 and 4431 with other classes.
    /// </summary>
    public static IReadOnlyList<(int Id, string ClassName)> BlockedFileControls { get; } =
    [
        (4418, "Static"), (65535, "Static"), (4431, "Static"), (65535, "Static"), (4419, "Static"), (4417, "SysLink"),
        (1, "Button")
    ];

    /// <summary>
    /// Makes <paramref name="dialog"/> a window of class <paramref name="className"/> whose children are
    /// <paramref name="controls"/> (by default <see cref="Controls"/>), with handles right after the dialog's.
    /// </summary>
    public static void Add(
        Mock<INativeWindowWrapper> native, nint dialog, IEnumerable<(int Id, string ClassName)>? controls = null,
        string className = "#32770")
    {
        native.Setup(n => n.GetClassName(dialog)).Returns(className);
        native.Setup(n => n.GetAncestor(dialog, WindowAncestor.Root)).Returns(dialog);

        var children = (controls ?? Controls).Select((control, i) => (Handle: dialog + 1 + i, control.Id, control.ClassName)).ToList();
        foreach (var child in children)
        {
            native.Setup(n => n.GetWindowLongPtr(child.Handle, GwlpId)).Returns(new nint(child.Id));
            native.Setup(n => n.GetClassName(child.Handle)).Returns(child.ClassName);
            native.Setup(n => n.GetAncestor(child.Handle, WindowAncestor.Root)).Returns(dialog);
            native.Setup(n => n.IsWindow(child.Handle)).Returns(true);
        }

        native.Setup(n => n.EnumChildWindows(dialog, It.IsAny<Func<nint, bool>>()))
            .Callback((nint _, Func<nint, bool> callback) =>
            {
                foreach (var child in children)
                {
                    if (!callback(child.Handle))
                    {
                        break;
                    }
                }
            });
    }

    /// <summary>The handle of the <paramref name="index"/>th control that <see cref="Add"/> gave <paramref name="dialog"/>.</summary>
    public static nint Control(nint dialog, int index) => dialog + 1 + index;
}
