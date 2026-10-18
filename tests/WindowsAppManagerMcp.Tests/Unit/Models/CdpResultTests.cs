using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace WindowsAppManagerMcp.Tests.Unit.Models;

/// <summary>
/// CdpResult ties Success to Value: only its factories make one, a success always has its value, and a success is never
/// copied as if it were a failure.
/// </summary>
public class CdpResultTests
{
    [Fact]
    public void Success_TellsTheCompilerThatValueIsSet()
    {
        var attribute = typeof(CdpResult<string>).GetProperty(nameof(CdpResult<string>.Success))!
            .GetCustomAttribute<MemberNotNullWhenAttribute>();

        attribute.Should().NotBeNull();
        attribute!.ReturnValue.Should().BeTrue();
        attribute.Members.Should().Equal(nameof(CdpResult<string>.Value));
    }

    [Fact]
    public void OnlyTheFactoriesMakeOne()
    {
        typeof(CdpResult<string>).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty(
            "a constructor would make a success without a value, or a failure without an error code");
    }

    [Fact]
    public void Ok_IsASuccessWithItsValue()
    {
        var result = CdpResult<string>.Ok("tab");

        result.Success.Should().BeTrue();
        result.Value.Should().Be("tab");
        result.ErrorCode.Should().Be(BrowserErrorCode.None);
        result.Error.Should().BeNull();
        result.DescribeError().Should().BeNull();
    }

    [Fact]
    public void Ok_RefusesNull()
    {
        var ok = () => CdpResult<string>.Ok(null!);

        ok.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ok_TakesAValueType()
    {
        CdpResult<bool>.Ok(false).Should().Match<CdpResult<bool>>(r => r.Success && r.Value == false);
    }

    [Fact]
    public void Fail_IsAFailureWithItsCodeAndMessage()
    {
        var result = CdpResult<string>.Fail(BrowserErrorCode.CdpTimeout, "No answer.");

        result.Success.Should().BeFalse();
        result.Value.Should().BeNull();
        result.DescribeError().Should().Be("CdpTimeout: No answer.");
    }

    [Fact]
    public void Fail_RefusesTheCodeOfASuccess()
    {
        // A failure coded None would read as a success: for a value type, its default Value is not null
        var fail = () => CdpResult<bool>.Fail(BrowserErrorCode.None, "Nothing went wrong.");

        fail.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("errorCode");
    }

    [Fact]
    public void FailAs_CopiesTheFailure()
    {
        var copied = CdpResult<string>.Fail(BrowserErrorCode.CdpTargetClosed, "Gone.").FailAs<int>();

        copied.Success.Should().BeFalse();
        copied.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetClosed);
        copied.Error.Should().Be("Gone.");
    }

    [Fact]
    public void FailAs_KeepsTheTabTheFailureLeftOpen()
    {
        // A new tab the browser opened before the call failed: the caller reports it whatever type the failure is copied to
        var copied = (CdpResult<string>.Fail(BrowserErrorCode.CdpPortOwnerMismatch, "Changed hands.") with { OpenedTargetId = "NEW1" })
            .FailAs<int>();

        copied.OpenedTargetId.Should().Be("NEW1");
        CdpResult<string>.Fail(BrowserErrorCode.CdpTimeout, "No answer.").OpenedTargetId.Should().BeNull();
    }

    [Fact]
    public void FailAs_OnASuccess_Throws()
    {
        // It used to return a result with no error code and no value, which reads as a success whose Value is null
        var copy = () => CdpResult<string>.Ok("tab").FailAs<int>();

        copy.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void With_KeepsTheValueAndTheCode()
    {
        var failure = CdpResult<string>.Fail(BrowserErrorCode.CdpTimeout, "No answer.");

        var explained = failure with { Error = "No answer. Page.enable was refused." };

        explained.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        explained.Error.Should().Be("No answer. Page.enable was refused.");
    }
}
