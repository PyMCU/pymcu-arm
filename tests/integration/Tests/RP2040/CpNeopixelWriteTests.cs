using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// neopixel_write.neopixel_write(digitalinout, buf) on the Feather RP2040, as the guides call it:
/// the bytes of the buffer reach the onboard pixel's pin (GP16) in order, most significant bit
/// first, as the PIO WS2812 waveform. Built from board.NEOPIXEL and a DigitalInOut, which makes
/// the pin a SIO output first and hands it to PIO afterwards.
/// </summary>
[TestFixture]
public class CpNeopixelWriteTests
{
    private const double SysClkHz = 125_000_000;
    private const int Pin = 16;

    // The last byte ends in a zero bit on purpose: RP2040Sharp applies a stalled instruction's
    // side-set only when it completes, so after a final one the emulated pad stays high.
    private const string Program = @"
import board
import digitalio
import neopixel_write

pin = digitalio.DigitalInOut(board.NEOPIXEL)
pin.direction = digitalio.Direction.OUTPUT
neopixel_write.neopixel_write(pin, bytearray([0x00, 0xFF, 0x40, 0x02]))
while True:
    pass
";

    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware()
        => _firmware = PymcuCompiler.BuildSourceRp2040(Program, stdlib: "circuitpython", board: "adafruit_feather_rp2040");

    [Test]
    public void TheBufferGoesOutByteByByteMostSignificantBitFirst()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);

        bool last = false;
        long riseAt = -1;
        var bits = new System.Text.StringBuilder();
        long end = (long)(300 * SysClkHz / 1e6);
        while (pico.Cpu.Cycles < end)
        {
            pico.RunCycles(1);
            bool v = pico.Rp2040.IoBank0.GetPadOutputEnable(Pin) && pico.Rp2040.IoBank0.GetPadOutputLevel(Pin);
            if (v == last) continue;
            if (v) riseAt = pico.Cpu.Cycles;
            else bits.Append((pico.Cpu.Cycles - riseAt) * 1e9 / SysClkHz > 550 ? '1' : '0');
            last = v;
        }

        bits.ToString().Should().Be("00000000" + "11111111" + "01000000" + "00000010");
    }

    [Test]
    public void ThePinBelongsToPioAfterTheCall()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);

        pico.RunMicroseconds(300);

        pico.Gpio[Pin].IsPioOutput.Should().BeTrue("neopixel_write routes the pin to PIO0 (FUNCSEL 6)");
    }
}
