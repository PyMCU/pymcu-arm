using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.TestKit.Probes;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// f32 on RP2350: arithmetic, comparisons and float-int conversions lowered to
/// the Cortex-M33 FPU (FPv5-SP, softfp -- crt0_m33 enables CPACR at reset).
/// Same firmware contract as the RP2040 FloatTests, which lowers the identical
/// source to __aeabi_f* shims over the bootrom fast-float library.
/// 2.5*4+1.5=11.5, /2=5.75>5 -> G; int(5.75*10)=57; int(-2.5)=-2 (toward zero).
/// </summary>
[TestFixture]
public class FloatTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("float-rp2350");

    [Test]
    public void ArithmeticComparisonsAndConversions()
    {
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(_firmware)
            .AddUartProbe(0, out UartProbe uart);
        sim.WaitForUart(uart, "-2.5", timeoutMs: 20_000).ConditionMet.Should().BeTrue();
        uart.Text.Should().Contain("G");
        uart.Text.Should().Contain("57");
        uart.Text.Should().Contain("-2");
        uart.Text.Should().Contain("5.7");   // print(float), one-decimal contract
    }
}
