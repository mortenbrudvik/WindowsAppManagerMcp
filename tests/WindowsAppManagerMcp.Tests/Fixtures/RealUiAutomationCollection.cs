namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// The test classes that call the real UI Automation client. They only read (the desktop root, and
/// handles that are not windows), but the client's first call in a process fails with E_FAIL when
/// another thread makes its first call at the same time (see <see cref="UiaClientStartup"/>), and
/// xUnit runs test classes in parallel. Classes in this collection run one at a time, after the
/// parallel tests, so they neither race each other nor any other test.
/// </summary>
/// <remarks>Put every new test class that reaches the real UI Automation client here.</remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealUiAutomationCollection
{
    public const string Name = "Real UI Automation";
}
