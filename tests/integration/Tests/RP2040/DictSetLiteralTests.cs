using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Closed dict/set literals on RP2040 (same fixture contract as the AVR
/// DictSetLiteralTests): const/runtime key lookups, KeyError caught via the
/// exception model, set membership, len(), string-keyed constant lookup.
/// </summary>
[TestFixture]
public class DictSetLiteralTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("dict-set-rp2040");

    private PicoSimulation Sim()
    {
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        return pico;
    }

    [Test]
    public void LookupsMembershipAndKeyError()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "M:2", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("V:30");
        pico.Uart0.Should().Contain("R:20");
        pico.Uart0.Should().Contain("E:caught");
        pico.Uart0.Should().NotContain("E:missed");
        pico.Uart0.Should().Contain("S:1");
        pico.Uart0.Should().Contain("S:0");
        pico.Uart0.Should().NotContain("S:bad");
        pico.Uart0.Should().Contain("N:3");
    }
}
