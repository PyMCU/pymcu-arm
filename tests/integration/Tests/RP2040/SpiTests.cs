using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class SpiTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("spi-rp2040");

    [Test]
    public void Master_ClocksOutBytes()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);

        // Capture every byte the master clocks out on MOSI; respond with 0.
        var mosi = new List<byte>();
        pico.Rp2040.Spi0.OnTransfer = tx => { mosi.Add((byte)tx); return 0; };

        pico.RunMilliseconds(5);

        mosi.Should().Contain(new byte[] { 0xAB, 0xCD, 0xEF },
            "the firmware transfers 0xAB, 0xCD, 0xEF on SPI0");
    }
}
