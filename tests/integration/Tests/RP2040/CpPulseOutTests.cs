using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// pulseio.PulseOut on the Feather RP2040, as adafruit_irremote transmits: the carrier and the
/// durations come through the CircuitPython layer onto the HAL's PulseTrain unchanged.
/// </summary>
[TestFixture]
public class CpPulseOutTests
{
    private const int Pin = 7;          // board.D5

    private const string Program = @"
import board
import pulseio

out = pulseio.PulseOut(board.D5, frequency=38000, duty_cycle=32768)

def main():
    out.send([800, 400, 800, 400])
    while True:
        pass
";

    [Test]
    public void SendKeepsTheCarrierOnForTheOddDurationsOnly()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Program, stdlib: "circuitpython", board: "adafruit_feather_rp2040");
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        var rises = new List<double>();
        bool last = false;
        long start = pico.Cpu.Cycles;
        while (pico.Cpu.Cycles < start + 4_000 * 125)
        {
            pico.RunCycles(10);
            bool now = pico.Rp2040.IoBank0.GetPadOutputEnable(Pin) && pico.Rp2040.IoBank0.GetPadOutputLevel(Pin);
            if (now && !last) rises.Add((pico.Cpu.Cycles - start) / 125.0);
            last = now;
        }

        rises.Should().NotBeEmpty();
        double first = rises[0];
        rises.Count(r => r < first + 800).Should().BeInRange(29, 31, "800 us of 38 kHz");
        rises.Count(r => r >= first + 800 + 30 && r < first + 1200 - 30).Should().Be(0, "400 us off");
        rises.Count(r => r >= first + 1200 + 30 && r < first + 2000).Should().BeInRange(28, 31, "the second 800 us");
        rises.Count(r => r >= first + 2000 + 60).Should().Be(0, "off after the last duration");
    }
}
