using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.TestKit.Probes;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// Pulse capture on the RP2350 (hal/rp/pulse.py): the RP2040's PIO program with the divider for
/// 150 MHz, and the RP2350's own DMA control-register layout. Same checks: edge-to-edge widths in
/// microseconds, and a CPU that sleeps while the frame arrives, with a full ring keeping the newest.
/// </summary>
[TestFixture]
public class PulseCaptureTests
{
    private const int Pin = 10;

    private const string Reader = @"
from pymcu.hal.pulse import PulseCapture

cap = PulseCapture(10, 100, 0)

def main():
    while True:
        if cap.count() > 0:
            print(cap.popleft())
";

    private const string Sleeper = @"
from pymcu.hal.pulse import PulseCapture
from pymcu.time import delay_ms

cap = PulseCapture(10, 4, 0)

def main():
    delay_ms(20)
    print(cap.count())
    while cap.count() > 0:
        print(cap.popleft())
";

    private static byte[] Build(string program) => PymcuCompiler.BuildSourceRp2350(program, frequency: 150_000_000);

    private static RP2350TestSimulation Boot(string program, out UartProbe uart)
        => RP2350TestSimulation.Create(CpuArchitecture.Arm).WithBinary(Build(program)).AddUartProbe(0, out uart);

    private static void Hold(RP2350TestSimulation sim, bool high, double us)
    {
        sim.SetGpioInput(Pin, high);
        sim.RunMicroseconds(us, sliceSize: 8);
    }

    private static void Frame(RP2350TestSimulation sim, params double[] widths)
    {
        bool level = true;
        foreach (var w in widths) { Hold(sim, level, w); level = !level; }
        Hold(sim, level, 50);
    }

    private static List<int> Numbers(UartProbe uart)
        => uart.Lines.Where(l => int.TryParse(l.Trim(), out _)).Select(l => int.Parse(l.Trim())).ToList();

    [Test]
    public void EveryIntervalBetweenTwoEdgesIsMeasuredInMicroseconds()
    {
        using var sim = Boot(Reader, out var uart);
        sim.RunMicroseconds(300, sliceSize: 8);

        Frame(sim, 100, 200, 50, 300, 1000);
        sim.RunMilliseconds(40);

        var got = Numbers(uart);
        got.Should().HaveCount(5);
        var want = new[] { 100, 200, 50, 300, 1000 };
        for (int i = 0; i < want.Length; i++)
            got[i].Should().BeInRange(want[i] - 4, want[i] + 2, $"interval {i}");
        sim.HardFaultCount.Should().Be(0);
    }

    [Test]
    public void TheCpuCanSleepWhileAFrameArrivesAndAFullRingKeepsTheNewest()
    {
        using var sim = Boot(Sleeper, out var uart);
        sim.RunMicroseconds(300, sliceSize: 8);

        Frame(sim, 100, 110, 120, 130, 140, 150, 160);
        sim.RunMilliseconds(40);

        var got = Numbers(uart);
        got.Should().HaveCount(5);
        got[0].Should().Be(4);
        var newest = new[] { 130, 140, 150, 160 };
        for (int i = 0; i < 4; i++)
            got[1 + i].Should().BeInRange(newest[i] - 4, newest[i] + 2);
    }
}
