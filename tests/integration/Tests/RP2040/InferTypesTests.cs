using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Type inference for unannotated params/returns on RP2040 (same fixture
/// contract as the AVR InferTypesTests): uint16/int16/uint32 inferred from
/// call-site evidence instead of the truncating uint8 default.
/// </summary>
[TestFixture]
public class InferTypesTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("infer-types-rp2040");

    [Test]
    public void InferredWidths_EndToEnd()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        pico.RunUntilOutput(pico.Uart0, "Q:65540", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("R:600");
        pico.Uart0.Should().Contain("S:-15");
    }
}
