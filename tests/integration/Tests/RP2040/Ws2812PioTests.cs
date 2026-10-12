using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// WS2812 pixels on the RP2040 (pymcu.hal.ws2812 -> hal/rp/ws2812.py): a PIO state machine makes
/// the 800 kHz waveform and the CPU only writes bytes to its FIFO. The emulator runs the PIO
/// program, so the pin's edges are the real ones and a bit is told from its high time, which is
/// all a strip uses: about 875 ns for a one and 250 ns for a zero, 1.25 us per bit.
/// </summary>
[TestFixture]
public class Ws2812PioTests
{
    private const double SysClkHz = 125_000_000;
    private const int Pin = 16;

    // Two frames: green, red, blue of one pixel, a latch, then a single byte. Both end in a zero
    // bit on purpose: RP2040Sharp applies a stalled instruction's side-set only when the
    // instruction completes, where the chip asserts it on the first cycle, so after a final ONE
    // bit the emulated line stays high until the next byte arrives instead of dropping to low.
    private const string Program = @"
from pymcu.hal.ws2812 import ws2812_init, ws2812_write_byte, ws2812_reset

def main():
    ws2812_init(16)
    ws2812_write_byte(16, 0xFF)
    ws2812_write_byte(16, 0x00)
    ws2812_write_byte(16, 0x80)
    ws2812_reset(16)
    ws2812_write_byte(16, 0xA4)
    ws2812_reset(16)
    while True:
        pass
";

    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildSourceRp2040(Program);

    private readonly record struct Edge(long Cycle, bool Level);

    private static List<Edge> Edges(double microseconds)
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        var edges = new List<Edge>();
        bool last = Level(pico);
        long end = (long)(microseconds * SysClkHz / 1e6);
        while (pico.Cpu.Cycles < end)
        {
            pico.RunCycles(1);
            bool v = Level(pico);
            if (v != last) { edges.Add(new Edge(pico.Cpu.Cycles, v)); last = v; }
        }
        return edges;
    }

    // What the pad drives, whichever peripheral owns the pin. GpioPin.DigitalValue answers from SIO
    // alone, so a pin that was a SIO output before it was handed to PIO (a DigitalInOut does that)
    // would read as its stale SIO level.
    private static bool Level(PicoSimulation pico)
        => pico.Rp2040.IoBank0.GetPadOutputEnable(Pin) && pico.Rp2040.IoBank0.GetPadOutputLevel(Pin);

    private static double Ns(long cycles) => cycles * 1e9 / SysClkHz;

    /// <summary>The bits of each frame (a frame ends at a gap of 50 us or more) and the gaps.</summary>
    private static (List<string> Frames, List<double> GapsUs) Decode(List<Edge> edges)
    {
        var frames = new List<string>();
        var gaps = new List<double>();
        var bits = new System.Text.StringBuilder();
        long lastFall = -1;
        for (int i = 0; i + 1 < edges.Count; i++)
        {
            if (!edges[i].Level) continue;                       // a rising edge starts a bit
            if (lastFall >= 0)
            {
                double gap = Ns(edges[i].Cycle - lastFall) / 1000.0;
                if (gap >= 50) { frames.Add(bits.ToString()); bits.Clear(); gaps.Add(gap); }
            }
            double high = Ns(edges[i + 1].Cycle - edges[i].Cycle);
            bits.Append(high > 550 ? '1' : '0');
            lastFall = edges[i + 1].Cycle;
        }
        frames.Add(bits.ToString());
        return (frames, gaps);
    }

    [Test]
    public void TheBytesReachTheWireMostSignificantBitFirst()
    {
        var (frames, _) = Decode(Edges(400));

        frames.Should().Equal("111111110000000010000000", "10100100");
    }

    [Test]
    public void AOneIsHighForAboutEightHundredNanosecondsAndAZeroForAboutTwoFifty()
    {
        var edges = Edges(200);

        var highs = new List<double>();
        for (int i = 0; i + 1 < edges.Count; i++)
            if (edges[i].Level) highs.Add(Ns(edges[i + 1].Cycle - edges[i].Cycle));

        highs.Where(h => h > 550).Should().NotBeEmpty().And.OnlyContain(h => h >= 725 && h <= 1025,
            "a one is high for 7 of 10 PIO cycles at 8 MHz (875 ns)");
        highs.Where(h => h <= 550).Should().NotBeEmpty().And.OnlyContain(h => h >= 150 && h <= 400,
            "a zero is high for 2 of 10 PIO cycles (250 ns)");
    }

    [Test]
    public void EveryBitTakesOneAndAQuarterMicrosecond()
    {
        var edges = Edges(200);

        var periods = new List<double>();
        for (int i = 0; i + 2 < edges.Count; i++)
            if (edges[i].Level && edges[i + 2].Level && Ns(edges[i + 2].Cycle - edges[i].Cycle) < 2000)
                periods.Add(Ns(edges[i + 2].Cycle - edges[i].Cycle));

        periods.Should().NotBeEmpty().And.OnlyContain(p => p >= 1190 && p <= 1310);
    }

    [Test]
    public void ResetHoldsTheLineLowLongEnoughForTheStripToLatch()
    {
        var (_, gaps) = Decode(Edges(400));

        gaps.Should().ContainSingle().Which.Should().BeGreaterThanOrEqualTo(50,
            "more than 50 us low between the frames is what latches the pixels");
    }

    [Test]
    public void TheLineIsLowWhenNothingIsBeingSent()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);

        pico.RunMicroseconds(400);

        Level(pico).Should().BeFalse();
    }
}
