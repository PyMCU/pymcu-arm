using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.TestKit.Probes;

namespace PyMCU.IntegrationTests.Tests.RP2350;

[TestFixture]
public class UartEchoTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("uart-echo-rp2350");

    private static RP2350TestSimulation Sim(out UartProbe uart)
    {
        var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(_firmware)
            .AddUartProbe(0, out uart);
        return sim;
    }

    [Test]
    public void Boot_SendsEchoBanner()
    {
        using var sim = Sim(out var uart);
        sim.WaitForUart(uart, "ECHO", timeoutMs: 20_000).ConditionMet
            .Should().BeTrue("the firmware prints an ECHO banner at boot");
        uart.Contains("ECHO").Should().BeTrue();
        sim.HardFaultCount.Should().Be(0);
    }

    [Test]
    public void Echo_SingleByte()
    {
        using var sim = Sim(out var uart);
        sim.WaitForUart(uart, "ECHO", timeoutMs: 20_000);
        var before = uart.Count;

        sim.UartSend(0, "A");
        sim.RunUntil(() => uart.Count > before,
                     maxInstructions: (long)(5_000.0 * RP2350Machine.CLK_HZ / 1000.0) * 2)
            .ConditionMet.Should().BeTrue("the firmware should echo the injected byte");

        uart.RawBytes[^1].Should().Be((byte)'A');
    }
}
