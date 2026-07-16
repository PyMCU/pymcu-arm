using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// f32 on RP2040: arithmetic, comparisons and float-int conversions lowered to
/// __aeabi_f* shims over the bootrom fast-float library (crt0 resolves the ROM
/// SF table at reset). 2.5*4+1.5=11.5, /2=5.75>5 -> G; int(5.75*10)=57;
/// int(-2.5)=-2 (toward zero); int(7.0/2.0)=3.
/// </summary>
[TestFixture]
public class FloatTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("float-rp2040");

    [Test]
    public void ArithmeticComparisonsAndConversions()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        pico.RunUntilOutput(pico.Uart0, "-2.5", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("G");
        pico.Uart0.Should().Contain("57");
        pico.Uart0.Should().Contain("-2");
        pico.Uart0.Should().Contain("5.7");   // print(float), one-decimal contract
    }
}
