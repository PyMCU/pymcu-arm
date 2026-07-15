using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// pymcu.collections.FixedDict on RP2040 (same fixture contract as the AVR
/// FixedDictTests): insert/overwrite, membership, len, get default, pop +
/// KeyError caught, ValueError on full, clear.
/// </summary>
[TestFixture]
public class FixedDictTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("fixeddict-rp2040");

    private PicoSimulation Sim()
    {
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        return pico;
    }

    [Test]
    public void FullSemantics_EndToEnd()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "Z:0", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("G:6");
        pico.Uart0.Should().Contain("G2:7");
        pico.Uart0.Should().Contain("C:1").And.Contain("C:0");
        pico.Uart0.Should().Contain("L:2");
        pico.Uart0.Should().Contain("D:99");
        pico.Uart0.Should().Contain("P:7");
        pico.Uart0.Should().Contain("E:caught").And.NotContain("E:missed");
        pico.Uart0.Should().Contain("F:caught").And.NotContain("F:missed");
    }
}
