using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// The carrier half of pulseio.PulseOut on the RP2040 (pymcu.hal.pulse.PulseTrain -> hal/rp/pulse.py):
/// a PWM slice at the carrier frequency, handed to the pin for the "on" durations and taken back with the
/// pin held low for the "off" ones. The tests watch the pad the way an infrared LED's driver would.
/// </summary>
[TestFixture]
public class PulseTrainTests
{
    private const int Pin = 12;
    private const double CyclesPerUs = 125;

    private const string Sender = @"
from pymcu.hal.pulse import PulseTrain
from pymcu.time import delay_ms
from pymcu.types import uint16

def main():
    t = PulseTrain(12, 38000, 21845)
    delay_ms(1)
    p: uint16[4] = [1000, 500, 600, 400]
    t.send(p, 4)
    while True:
        pass
";

    private static bool Level(PicoSimulation pico)
        => pico.Rp2040.IoBank0.GetPadOutputEnable(Pin) && pico.Rp2040.IoBank0.GetPadOutputLevel(Pin);

    [Test]
    public void TheCarrierRunsAtItsFrequencyDuringOnDurationsAndTheLineIsLowDuringOffOnes()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(PymcuCompiler.BuildSourceRp2040(Sender));

        // Sample the pad every ~0.1 us, recording rising edges.
        var rises = new List<double>();
        bool last = false;
        long start = pico.Cpu.Cycles;
        long end = start + (long)(12_000 * CyclesPerUs);
        while (pico.Cpu.Cycles < end)
        {
            pico.RunCycles(10);
            bool now = Level(pico);
            if (now && !last) rises.Add((pico.Cpu.Cycles - start) / CyclesPerUs);
            last = now;
        }

        rises.Should().NotBeEmpty("the carrier reached the pad");
        double first = rises[0];
        // The frame is 1000 on, 500 off, 600 on, 400 off, from the first rise.
        // The carrier runs free, so a burst starts up to one period (26 us) after its nominal start.
        var inFirst = rises.Where(r => r >= first && r < first + 1000).ToList();
        var inGap = rises.Where(r => r >= first + 1000 + 30 && r < first + 1500 - 30).ToList();
        var inSecond = rises.Where(r => r >= first + 1500 + 30 && r < first + 2100).ToList();
        var after = rises.Where(r => r >= first + 2100 + 60).ToList();

        inFirst.Count.Should().BeInRange(36, 39, "1000 us of a 38 kHz carrier is 38 periods");
        inGap.Should().BeEmpty("the carrier is off for 500 us");
        inSecond.Count.Should().BeInRange(21, 24, "600 us of a 38 kHz carrier is 23 periods");
        after.Should().BeEmpty("the carrier stays off after the last duration");

        var period = inFirst.Zip(inFirst.Skip(1), (a, b) => b - a).OrderBy(x => x).ToList();
        period[period.Count / 2].Should().BeInRange(25.5, 27.2, "the median period is 1/38 kHz = 26.3 us");
    }
}
