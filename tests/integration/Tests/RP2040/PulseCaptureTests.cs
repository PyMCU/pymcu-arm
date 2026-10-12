using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Pulse capture on the RP2040 (pymcu.hal.pulse -> hal/rp/pulse.py): a PIO state machine times
/// every interval between two edges in microseconds and a DMA channel stores the words into a ring,
/// so the CPU can sleep while a frame arrives. The tests drive the pin from outside, the way a
/// sensor or a remote would, and read back what the firmware prints.
/// </summary>
[TestFixture]
public class PulseCaptureTests
{
    private const int Pin = 10;

    // The CPU does nothing for 20 ms (a sleep, as adafruit_irremote and adafruit_dht do) while the
    // pulses arrive; then it reports how many were kept and each, oldest first.
    private const string Sleeper = @"
from pymcu.hal.pulse import PulseCapture
from pymcu.time import delay_ms

cap = PulseCapture(10, 4, 0)

def main():
    delay_ms(20)
    print(cap.count())
    while cap.count() > 0:
        print(cap.popleft())
    print('END')
";

    // Keeps up: prints each pulse as it comes.
    private const string Reader = @"
from pymcu.hal.pulse import PulseCapture

cap = PulseCapture(10, 100, 0)

def main():
    while True:
        if cap.count() > 0:
            print(cap.popleft())
";

    // clear() and pause()/resume() in a row, announcing each step on the console.
    private const string Controls = @"
from pymcu.hal.pulse import PulseCapture
from pymcu.time import delay_ms

cap = PulseCapture(10, 50, 0)

def main():
    delay_ms(10)
    cap.clear()
    print(cap.count())
    delay_ms(10)
    print(cap.count())
    cap.pause()
    delay_ms(10)
    print(cap.paused())
    cap.resume()
    print(cap.paused())
    delay_ms(10)
    print(cap.count())
    print('END')
";

    private static byte[] Build(string program) => PymcuCompiler.BuildSourceRp2040(program);

    private static PicoSimulation Boot(string program)
    {
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(Build(program));
        return pico;
    }

    // RunMicroseconds runs a batch of INSTRUCTIONS and overshoots by the cycles-per-instruction
    // ratio, which would stretch every pulse being generated; these run to a CPU cycle count.
    private static void RunFor(PicoSimulation pico, double us)
    {
        long end = pico.Cpu.Cycles + (long)(us * 125);
        while (pico.Cpu.Cycles < end) pico.RunCycles(1);
    }

    /// <summary>Hold the pin at <paramref name="high"/> for <paramref name="us"/> microseconds.</summary>
    private static void Hold(PicoSimulation pico, bool high, double us)
    {
        pico.Gpio[Pin].ForceInput(high);
        RunFor(pico, us);
    }

    private static void Frame(PicoSimulation pico, params double[] widths)
    {
        bool level = true;                       // the line rests low; the first edge is a rise
        foreach (var w in widths) { Hold(pico, level, w); level = !level; }
        Hold(pico, level, 50);
    }

    private static List<int> Numbers(PicoSimulation pico)
        => pico.Uart0.Lines.Where(l => int.TryParse(l.Trim(), out _)).Select(l => int.Parse(l.Trim())).ToList();

    [Test]
    public void EveryIntervalBetweenTwoEdgesIsMeasuredInMicroseconds()
    {
        using var pico = Boot(Reader);
        pico.RunMicroseconds(300);                           // the firmware is up and listening

        // rise (reference), 100 high, 200 low, 50 high, 300 low, 1000 high, then the line settles.
        Frame(pico, 100, 200, 50, 300, 1000);
        pico.RunMicroseconds(40_000);                        // the console prints at 115200 baud

        var got = Numbers(pico);
        got.Should().HaveCount(5);
        var want = new[] { 100, 200, 50, 300, 1000 };
        for (int i = 0; i < want.Length; i++)
            got[i].Should().BeInRange(want[i] - 4, want[i] + 1, $"interval {i}: PIO counts whole microseconds and loses the three cycles between intervals");
    }

    [Test]
    public void TheCpuCanSleepWhileAFrameArrivesAndAFullRingKeepsTheNewest()
    {
        using var pico = Boot(Sleeper);
        pico.RunMicroseconds(300);

        // Seven intervals while the firmware sleeps; it holds four.
        Frame(pico, 100, 110, 120, 130, 140, 150, 160);
        pico.RunMilliseconds(40);

        var got = Numbers(pico);
        got.Should().HaveCount(5, "the count, then the four pulses it kept");
        got[0].Should().Be(4);
        var newest = new[] { 130, 140, 150, 160 };
        for (int i = 0; i < 4; i++)
            got[1 + i].Should().BeInRange(newest[i] - 4, newest[i] + 1);
    }

    [Test]
    public void ClearForgetsWhatWasHeldAndPauseDropsWhatArrivesMeanwhile()
    {
        using var pico = Boot(Controls);
        pico.RunMicroseconds(300);

        // Before clear(): a burst that clear() must throw away.
        Frame(pico, 100, 100, 100);
        pico.RunMilliseconds(12);                            // past clear() and its print
        // After clear(): a burst the firmware counts.
        Frame(pico, 100, 100, 100);
        pico.RunMilliseconds(10);                            // the count after it
        // While paused: a burst nobody should see.
        Frame(pico, 100, 100, 100);
        pico.RunMilliseconds(10);
        // After resume(): a new burst; its first edge only sets the reference.
        pico.RunMilliseconds(5);
        Frame(pico, 100, 100, 100);
        pico.RunMilliseconds(40);

        var got = Numbers(pico);
        got.Should().Equal(0, 3, 1, 0, 3);
    }
}
