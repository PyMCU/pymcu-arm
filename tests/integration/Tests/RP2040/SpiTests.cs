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

    // configure() reprogramming an already-built bus, not only the constructor's own
    // one-time setup -- the shape busio.SPI.configure() needs. Verifies both the
    // reprogram actually took (the transfer after it still clocks the right bytes) and
    // the ACTUAL rate (not the one asked for: SCR/CPSDVSR are not exact dividers).
    [Test]
    public void Configure_ReprogramsRateAndStillTransfers()
    {
        var firmware = PymcuCompiler.BuildRp2040("spi-configure-rp2040");
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        var mosi = new List<byte>();
        pico.Rp2040.Spi0.OnTransfer = tx => { mosi.Add((byte)tx); return 0; };

        pico.RunUntilOutput(pico.Uart0, "1008064", timeoutMs: 20_000)
            .Should().BeTrue("1 MHz at construction lands on 125e6/(2*62) = 1008064, not exactly 1e6");
        pico.RunUntilOutput(pico.Uart0, "2016129", timeoutMs: 20_000)
            .Should().BeTrue("configure(2 MHz) lands on 125e6/(2*31) = 2016129 after reprogramming");

        mosi.Should().Contain(new byte[] { 0xAB, 0xCD, 0xEF },
            "the transfer after configure() still clocks real bytes, not a frozen or corrupted bus");
    }
}
