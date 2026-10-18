namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// <see cref="GateObserver"/>, which SelfGatedToolsContractTests watches the tools' services with: it must watch every member
/// of a service, those it inherits from another interface too, and forward every call. The service here is a mock; nothing
/// takes the input gate.
/// </summary>
public class GateObserverTests
{
    /// <summary>A service's base interface, with a member that changes something.</summary>
    public interface ISampleBase
    {
        void Change();
    }

    /// <summary>A service that inherits a member from <see cref="ISampleBase"/>, and reads.</summary>
    public interface ISample : ISampleBase
    {
        int Read();
    }

    [Fact]
    public void WatchEveryChange_WatchesAMemberTheServiceInherits()
    {
        // Arrange
        var inner = new Mock<ISample>();
        inner.Setup(s => s.Read()).Returns(7);
        var observer = new GateObserver(InputGate.TagFlow());

        // Act
        var watched = (ISample)observer.WatchEveryChange(typeof(ISample), inner.Object, new HashSet<string> { nameof(ISample.Read) }, []);
        watched.Change();
        var read = watched.Read();

        // Assert
        observer.Seen.Should().ContainSingle().Which.Should().Be(new GateObservation("ISample.Change", GateHeld: false));
        read.Should().Be(7, "every call is forwarded");
        inner.Verify(s => s.Change(), Times.Once);
    }

    [Fact]
    public void Watch_RefusesAMethodTheServiceDoesNotHave()
    {
        // Arrange
        var observer = new GateObserver(InputGate.TagFlow());

        // Act
        var watch = () => observer.Watch(typeof(ISample), Mock.Of<ISample>(), [new WatchedCall("Missing")]);

        // Assert
        watch.Should().Throw<ArgumentException>().WithMessage("*no method Missing*");
    }
}
