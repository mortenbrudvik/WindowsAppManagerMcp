global using FluentAssertions;
global using Xunit;
global using Xunit.Abstractions;

global using WinDesktopMcp.E2ETests.Harness;
global using WinDesktopMcp.FixtureApp;
global using WinDesktopMcp.Models;
global using WinDesktopMcp.Services.Interfaces;
global using WinDesktopMcp.Tests.Infrastructure;

// One test at a time: the tests share the desktop (foreground, focus, the server's input gate), and a
// fixture started by one test must not be activated or covered by another test's fixture.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
