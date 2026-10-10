global using FluentAssertions;
global using Xunit;
global using Xunit.Abstractions;

global using WindowsAppManagerMcp.E2ETests.Harness;
global using WindowsAppManagerMcp.FixtureApp;
global using WindowsAppManagerMcp.Models;
global using WindowsAppManagerMcp.Services.Interfaces;
global using WindowsAppManagerMcp.Tests.Infrastructure;

// One test at a time: the tests share the desktop (foreground, focus, the server's input gate), and a
// fixture started by one test must not be activated or covered by another test's fixture.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
