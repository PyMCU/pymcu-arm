using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.TestKit.Probes;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// Portable T-flag exception model on RP2350 (Cortex-M33): same firmware
/// contract as the RP2040 ExceptionsTests, over the RP2350 UART0 base.
/// </summary>
[TestFixture]
public class ExceptionsTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("exceptions-rp2350");

    private static RP2350TestSimulation Sim(out UartProbe uart)
    {
        var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(_firmware)
            .AddUartProbe(0, out uart);
        return sim;
    }

    [Test]
    public void Raise_CaughtByExcept_AndTypeDiscrimination()
    {
        using var sim = Sim(out var uart);
        sim.WaitForUart(uart, "C:type", timeoutMs: 20_000).ConditionMet.Should().BeTrue();
        uart.Text.Should().Contain("A:caught").And.Contain("B:ok");
        uart.Text.Should().NotContain("A:missed").And.NotContain("C:value");
    }

    [Test]
    public void UncaughtRaise_HaltsPrintingExceptionName()
    {
        using var sim = Sim(out var uart);
        sim.WaitForUart(uart, "E:KeyError", timeoutMs: 20_000).ConditionMet
            .Should().BeTrue("an uncaught raise reaches __pymcu_unhandled_exn, which prints the name");
        uart.Text.Should().Contain("D:local").And.Contain("E:fin");
        uart.Text.Should().NotContain("F:missed");
    }
}
