using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class I2cTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("i2c-rp2040");

    [Test]
    public void Master_WritesToDevice()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);

        var writes = new List<byte>();
        pico.Rp2040.I2c0.OnWrite = (addr, data) => { if (addr == 0x3C) writes.Add(data); };

        pico.RunMilliseconds(5);

        writes.Should().Contain(new byte[] { 0x12, 0x34 },
            "the firmware writes 0x12 and 0x34 to the device at 0x3C");
    }

    // A real register READ, not only a write: write_then_read's RESTART between the
    // write phase (the register address) and the read phase -- the same
    // write-register-then-read-into transaction adafruit_bmp280._read_register uses
    // (one real device's chip-id register, BMP280's own 0xD0 -> 0x58). Covers the gap
    // the write-only test above leaves: before the read HAL existed, this shape could
    // not run on this chip at all (PyMCU#rp-i2c-spi).
    //
    // Direct HAL call, not through busio.I2C: busio.I2C.writeto_then_readfrom's own
    // call chain to this exact same HAL method hangs against this emulator for a
    // reason not yet isolated (reported separately) -- this pins the HAL half, which
    // is proven correct on its own.
    [Test]
    public void Master_ReadsChipIdRegister()
    {
        var firmware = PymcuCompiler.BuildRp2040("i2c-chipid-rp2040");
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        byte? registerWritten = null;
        pico.Rp2040.I2c0.OnWrite = (addr, data) => { if (addr == 0x77) registerWritten = data; };
        pico.Rp2040.I2c0.OnRead = addr => addr == 0x77 ? (byte)0x58 : (byte)0xFF;

        pico.RunUntilOutput(pico.Uart0, "RDY", timeoutMs: 20_000).Should().BeTrue("stage 1: boot");
        pico.RunUntilOutput(pico.Uart0, "READ_DONE", timeoutMs: 20_000).Should().BeTrue("stage 2: write_then_read returned");
        pico.RunUntilOutput(pico.Uart0, "88", timeoutMs: 20_000)
            .Should().BeTrue("the firmware prints chip_id[0] after the read, and 0x58 is 88 decimal");

        registerWritten.Should().Be(0xD0,
            "the firmware writes the chip-id register address (0xD0) before reading it back");
    }
}
