using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.TestKit.Probes;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// The carrier half of pulseio.PulseOut on the RP2350 (hal/rp/pulse.py): the same PWM slice gated by
/// the pin's function, with the 150 MHz clock behind the divider.
/// </summary>
[TestFixture]
public class PulseTrainTests
{
    private const int Pin = 12;

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

    [Test]
    public void TheCarrierRunsAtItsFrequencyDuringOnDurationsAndTheLineIsLowDuringOffOnes()
    {
        var firmware = PymcuCompiler.BuildSourceRp2350(Sender, frequency: 150_000_000);
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm).WithBinary(firmware);
        sim.SampleResolutionCycles = 2;
        var la = sim.AddLogicAnalyzer(Pin);

        sim.RunMicroseconds(12_000, sliceSize: 2);

        var rises = la.EdgesOf(Pin).Where(e => e.Level)
            .Select(e => e.Cycle * 1e6 / la.ClockHz).ToList();
        rises.Should().NotBeEmpty("the carrier reached the pad");
        double first = rises[0];
        var inFirst = rises.Where(r => r >= first && r < first + 1000).ToList();
        var inGap = rises.Where(r => r >= first + 1000 + 30 && r < first + 1500 - 30).ToList();
        var inSecond = rises.Where(r => r >= first + 1500 + 30 && r < first + 2100).ToList();
        var after = rises.Where(r => r >= first + 2100 + 60).ToList();

        inFirst.Count.Should().BeInRange(36, 39, "1000 us of a 38 kHz carrier is 38 periods");
        inGap.Should().BeEmpty("the carrier is off for 500 us");
        inSecond.Count.Should().BeInRange(21, 24, "600 us of a 38 kHz carrier is 23 periods");
        after.Should().BeEmpty("the carrier stays off after the last duration");
        la.FinalLevel(Pin).Should().BeFalse();
    }
}
