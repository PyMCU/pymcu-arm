using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// WS2812 pixels on the RP2350 (hal/rp/ws2812.py): the PIO program of the RP2040 with the
/// divider worked out for the 150 MHz the emulated part runs at. The exact bit-by-bit waveform
/// is measured on the RP2040 (Tests/RP2040/Ws2812PioTests), whose emulator hands a pin's level
/// over at every instruction; RP2350Sharp's logic analyzer sees it at a coarse step (66 cycles,
/// 440 ns here) that merges a 250 ns zero into its neighbours, so what is checked here is what
/// that step cannot blur: what reaches the FIFO, that the state machine drives the pin at the
/// bit rate, and that a reset leaves the line low.
/// </summary>
[TestFixture]
public class Ws2812PioTests
{
    private const int Pin = 16;
    private const int Sm = 3;

    private const string Frame = @"
from pymcu.hal.ws2812 import ws2812_init, ws2812_write_byte

def main():
    ws2812_init(16)
    ws2812_write_byte(16, 0xFF)
    ws2812_write_byte(16, 0x00)
    ws2812_write_byte(16, 0x80)
    while True:
        pass
";

    // Two frames with a reset between them. Each ends in a zero bit: RP2350Sharp applies a
    // stalled instruction's side-set when it completes, where the chip asserts it on the first
    // cycle, so after a final one the emulated line stays high until the next byte arrives.
    private const string Latch = @"
from pymcu.hal.ws2812 import ws2812_init, ws2812_write_byte, ws2812_reset

def main():
    ws2812_init(16)
    ws2812_write_byte(16, 0xFE)
    ws2812_reset(16)
    ws2812_write_byte(16, 0xA4)
    ws2812_reset(16)
    while True:
        pass
";

    private static byte[] _frame = null!, _latch = null!;

    [OneTimeSetUp]
    public void BuildFirmware()
    {
        _frame = PymcuCompiler.BuildSourceRp2350(Frame, frequency: 150_000_000);
        _latch = PymcuCompiler.BuildSourceRp2350(Latch, frequency: 150_000_000);
    }

    private static RP2350TestSimulation Boot(byte[] firmware)
        => RP2350TestSimulation.Create(CpuArchitecture.Arm).WithBinary(firmware);

    [Test]
    public void EachByteIsOneWordForStateMachine3WithTheByteOnTop()
    {
        using var sim = Boot(_frame);
        sim.AddPioProbe(0, out var pio);

        sim.RunMicroseconds(400);

        pio.TxOf(Sm).Should().Equal(0xFF000000u, 0x00000000u, 0x80000000u);
        pio.TxOf(0).Should().BeEmpty("the other state machines are not touched");
    }

    [Test]
    public void TheStateMachineDrivesThePinAtTheBitRate()
    {
        using var sim = Boot(_frame);
        sim.SampleResolutionCycles = 2;
        var la = sim.AddLogicAnalyzer(Pin);

        sim.RunMicroseconds(400, sliceSize: 2);

        // 24 bits are 30 us: the first rising edge starts them, a bit takes 1.25 us.
        var rises = la.EdgesOf(Pin).Where(e => e.Level).Select(e => e.Cycle).ToList();
        rises.Should().HaveCountGreaterThan(12);
        double span = (rises[^1] - rises[0]) * 1e6 / la.ClockHz;
        span.Should().BeInRange(20, 32, "the frame is 30 us long, less the bits a coarse sampler merges");
        la.FinalLevel(Pin).Should().BeFalse("the line is low when nothing is being sent");
    }

    [Test]
    public void ResetHoldsTheLineLowLongEnoughForTheStripToLatch()
    {
        using var sim = Boot(_latch);
        sim.SampleResolutionCycles = 2;
        var la = sim.AddLogicAnalyzer(Pin);

        sim.RunMicroseconds(400, sliceSize: 2);

        var edges = la.EdgesOf(Pin).Skip(1).ToList();
        double widestLowUs = 0;
        for (int i = 0; i + 1 < edges.Count; i++)
            if (!edges[i].Level)
                widestLowUs = Math.Max(widestLowUs, (edges[i + 1].Cycle - edges[i].Cycle) * 1e6 / la.ClockHz);

        widestLowUs.Should().BeGreaterThanOrEqualTo(50, "more than 50 us low between the frames latches the pixels");
    }
}
