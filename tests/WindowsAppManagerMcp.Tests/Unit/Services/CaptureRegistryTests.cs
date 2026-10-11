namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>The capture registry (roadmap M3.11): unique ids, a least-recently-used limit and a time to live.</summary>
public class CaptureRegistryTests
{
    private static CaptureGeometry Geometry(int offsetX = 0) => new(
        1568, 882, new ImageToScreenTransform(offsetX, 0, 2.44898, 2.44898), new CapturedRegion(offsetX, 0, 3840, 2160), null,
        new VirtualScreenBounds(0, 0, 3840, 2160));

    [Fact]
    public void Register_GivesEveryCaptureANewId_AndFindReturnsIt()
    {
        // Arrange
        var registry = new CaptureRegistry();

        // Act
        var first = registry.Register(Geometry(1));
        var second = registry.Register(Geometry(2));

        // Assert
        first.CaptureId.Should().NotBe(second.CaptureId);
        first.CaptureId.Should().MatchRegex("^c1-[0-9a-f]{6}$");
        second.CaptureId.Should().MatchRegex("^c2-[0-9a-f]{6}$");
        registry.Find(first.CaptureId)!.Geometry.ImageToScreen.OffsetX.Should().Be(1);
        registry.Find(second.CaptureId)!.Geometry.ImageToScreen.OffsetX.Should().Be(2);
    }

    [Fact]
    public void Ids_OfTwoRegistries_DoNotCollide()
    {
        // Two server runs: an id the agent kept from an earlier run must not find a capture of this run
        var ids = Enumerable.Range(0, 20).Select(_ => new CaptureRegistry().Register(Geometry()).CaptureId).ToList();

        ids.Distinct().Should().HaveCountGreaterThan(15, "each registry has its own random tag");
    }

    [Theory]
    [InlineData("")]
    [InlineData("c999-000000")]
    [InlineData("C1")]
    public void Find_UnknownId_IsNull(string captureId)
    {
        var registry = new CaptureRegistry();
        registry.Register(Geometry());

        registry.Find(captureId).Should().BeNull();
    }

    [Fact]
    public void Find_AfterTheTimeToLive_IsNull()
    {
        // Arrange
        var clock = new ManualClock();
        var registry = new CaptureRegistry(clock);
        var capture = registry.Register(Geometry());

        // Act / Assert - still there just before, gone at ten minutes, even though it was used in between
        clock.Advance(TimeSpan.FromMinutes(9));
        registry.Find(capture.CaptureId).Should().NotBeNull();
        clock.Advance(TimeSpan.FromMinutes(1));
        registry.Find(capture.CaptureId).Should().BeNull();
        registry.TimeToLive.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Register_BeyondTheCapacity_DropsTheLeastRecentlyUsed()
    {
        // Arrange
        var registry = new CaptureRegistry();
        var captures = Enumerable.Range(0, registry.Capacity).Select(_ => registry.Register(Geometry())).ToList();

        // Act - the oldest is used again, so the second oldest is dropped
        registry.Find(captures[0].CaptureId).Should().NotBeNull();
        var newest = registry.Register(Geometry());

        // Assert
        registry.Capacity.Should().Be(32);
        registry.Find(captures[0].CaptureId).Should().NotBeNull();
        registry.Find(captures[1].CaptureId).Should().BeNull();
        registry.Find(captures[2].CaptureId).Should().NotBeNull();
        registry.Find(newest.CaptureId).Should().NotBeNull();
    }

    [Fact]
    public void Register_StampsTheCaptureTime()
    {
        var clock = new ManualClock();
        var registry = new CaptureRegistry(clock);

        registry.Register(Geometry()).CapturedAt.Should().Be(clock.Now);
    }

    [Fact]
    public async Task Register_FromManyThreads_KeepsIdsUniqueAndTheLimit()
    {
        // Arrange
        var registry = new CaptureRegistry();

        // Act
        var ids = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => registry.Register(Geometry()).CaptureId)));

        // Assert
        ids.Should().OnlyHaveUniqueItems();
        ids.Count(id => registry.Find(id) != null).Should().Be(registry.Capacity);
    }
}
