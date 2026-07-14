using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Portable T-flag exception model on RP2040: raise propagates through the
/// @__pymcu_exn_flag/_code globals, except handlers discriminate on the code,
/// finally runs, and an uncaught raise halts via __pymcu_unhandled_exn
/// printing E:&lt;Name&gt; over UART0.
/// </summary>
[TestFixture]
public class ExceptionsTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("exceptions-rp2040");

    private PicoSimulation Sim()
    {
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        return pico;
    }

    [Test]
    public void Raise_CaughtByExcept_AndNoFalsePositives()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "B:ok", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("A:caught");
        pico.Uart0.Should().NotContain("A:missed");
        pico.Uart0.Should().NotContain("B:caught");
    }

    [Test]
    public void Handlers_DiscriminateByExceptionType()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "C:type", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().NotContain("C:value");
        pico.Uart0.Should().NotContain("C:missed");
    }

    [Test]
    public void RaiseDirectlyInTry_CaughtLocally_AndFinallyRuns()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "E:fin", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("D:local");
    }

    [Test]
    public void UncaughtRaise_HaltsPrintingExceptionName()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "E:KeyError", timeoutMs: 20_000)
            .Should().BeTrue("an uncaught raise reaches __pymcu_unhandled_exn, which prints the name");
        pico.Uart0.Should().NotContain("F:missed");
    }
}
