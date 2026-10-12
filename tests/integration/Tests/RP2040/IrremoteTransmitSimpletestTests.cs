using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Adafruit's irremote_transmit_simpletest.py, unmodified but for the pin, on the Feather RP2040:
/// GenericTransmit walks the header it was handed with <c>for i, _ in enumerate(self.header)</c> and
/// sends the NEC frame through pulseio.PulseOut. The pad is watched the way an IR LED's driver would
/// and the frame is read back from the carrier bursts and the silences between them.
/// </summary>
[TestFixture]
public class IrremoteTransmitSimpletestTests
{
    private const int Pin = 7;          // board.D5

    /// <summary>Marks (carrier present) and spaces, in microseconds, from the first frame.</summary>
    private static List<(bool Mark, double Us)> Frame()
    {
        var firmware = PymcuCompiler.BuildRp2040("irremote-transmit-simpletest-rp2040");
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        // A carrier period is 26 us: a stretch of the pad with no rising edge for 60 us is a space.
        var rises = new List<double>();
        bool last = false;
        long start = pico.Cpu.Cycles;
        while (pico.Cpu.Cycles < start + 100_000L * 125)
        {
            pico.RunCycles(10);
            bool now = pico.Rp2040.IoBank0.GetPadOutputEnable(Pin) && pico.Rp2040.IoBank0.GetPadOutputLevel(Pin);
            if (now && !last) rises.Add((pico.Cpu.Cycles - start) / 125.0);
            last = now;
        }

        var segments = new List<(bool, double)>();
        double markStart = rises[0], prev = rises[0];
        for (int i = 1; i < rises.Count; i++)
        {
            if (rises[i] - prev > 60)
            {
                segments.Add((true, prev - markStart + 26));
                segments.Add((false, rises[i] - prev - 26));
                markStart = rises[i];
            }
            prev = rises[i];
        }
        return segments;
    }

    [Test]
    public void TheHeaderIsTheNineAndAHalfThenFourAndAHalfMilliseconds()
    {
        var s = Frame();
        s.Count.Should().BeGreaterThan(4);
        s[0].Us.Should().BeInRange(9500 - 60, 9500 + 60);
        s[1].Us.Should().BeInRange(4500 - 60, 4500 + 60);
    }

    [Test]
    public void EachBitIsAMarkThenASpaceTheLengthItsValueSays()
    {
        var s = Frame();
        // 255 = eight ones (550 on, 550 off); 2 = 00000010: six zeros (550 on, 1700 off), a one, a zero.
        for (int b = 0; b < 8; b++)
        {
            s[2 + 2 * b].Us.Should().BeInRange(550 - 60, 550 + 60, $"bit {b} mark");
            s[3 + 2 * b].Us.Should().BeInRange(550 - 60, 550 + 60, $"bit {b} of 255 is a one");
        }
        for (int b = 0; b < 6; b++)
            s[3 + 2 * (8 + b)].Us.Should().BeInRange(1700 - 60, 1700 + 60, $"bit {b} of 2 is a zero");
        s[3 + 2 * 14].Us.Should().BeInRange(550 - 60, 550 + 60, "the seventh bit of 2 is a one");
        s[3 + 2 * 15].Us.Should().BeInRange(1700 - 60, 1700 + 60, "the last bit of 2 is a zero");
    }
}
