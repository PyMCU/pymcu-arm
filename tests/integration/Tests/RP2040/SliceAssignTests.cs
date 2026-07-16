using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Equal-length slice assignment on RP2040 (same fixture contract as AVR).
/// </summary>
[TestFixture]
public class SliceAssignTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("slice-assign-rp2040");

    [Test]
    public void LiteralCrossArrayOverlappingAndWhole()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        pico.RunUntilOutput(pico.Uart0, "C:871", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("A:19871");
        pico.Uart0.Should().Contain("B:871");
        pico.Uart0.Should().Contain("O:19198");
    }
}
