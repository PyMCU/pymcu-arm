using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class DmaTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("dma-rp2040");

    [Test]
    public void Copies_WordBetweenMemory()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        pico.RunMilliseconds(5);

        // The firmware DMAs 0xDEADBEEF and lights GP25 only if the copy arrived.
        pico.Gpio[25].Should().BeHigh("the DMA copied the word to the destination");
    }
}
