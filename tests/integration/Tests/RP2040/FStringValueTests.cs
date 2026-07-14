using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// f-strings as VALUES on RP2040: `s = f"..."` builds into a fixed buffer via
/// the shared pymcu.strfmt lowering (same fixture contract as the AVR
/// FStringValueTests -- formatting, len(s), s[i], loop reuse, format specs).
/// </summary>
[TestFixture]
public class FStringValueTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("fstring-value-rp2040");

    private PicoSimulation Sim()
    {
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        return pico;
    }

    [Test]
    public void Formats_IntSignedAndHexIntoBuffer_AndLen()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "L:20", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("t=23C reg=beef n=-42");
    }

    [Test]
    public void Indexing_LoopReuse_AndFormatSpecs()
    {
        using var pico = Sim();
        pico.RunUntilOutput(pico.Uart0, "pad=[  7]=[007]", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("B:t");
        pico.Uart0.Should().Contain("k=0 k=1 k=2 ");
    }
}
