using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// pulseio.PulseIn on the Feather RP2040, as adafruit_dht uses it: build it on a pin, pause and
/// clear it, resume it with a trigger (the host holds the line low, then lets go, which is the
/// start signal of a DHT), sleep while the sensor answers, pause and read what came. The sensor
/// is played from outside the way the chip would answer.
/// </summary>
[TestFixture]
public class CpPulseInTests
{
    private const int Pin = 7;          // board.D5

    private const string Program = @"
import board
import pulseio
import time

pulses = pulseio.PulseIn(board.D5, maxlen=100, idle_state=True)

def main():
    pulses.pause()
    pulses.clear()
    pulses.resume(1000)
    time.sleep(0.05)
    pulses.pause()
    print(len(pulses))
    print(pulses[0])
    print(pulses[1])
    print(pulses[2])
    print(pulses[3])
    print(pulses.popleft())
    print(len(pulses))
    pulses.clear()
    print(len(pulses))
";

    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware()
        => _firmware = PymcuCompiler.BuildSourceRp2040(Program, stdlib: "circuitpython", board: "adafruit_feather_rp2040");

    private static void RunTo(PicoSimulation pico, long cycle)
    {
        while (pico.Cpu.Cycles < cycle) pico.RunCycles(10);
    }

    private static void Play(PicoSimulation pico, bool level, double us)
    {
        pico.Gpio[Pin].ForceInput(level);
        RunTo(pico, pico.Cpu.Cycles + (long)(us * 125));
    }

    /// <summary>Runs until the host's start signal ends, then answers like a DHT22 (55.5 %, 21.5 C).</summary>
    private static List<int> RunAgainstASensor()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        pico.Gpio[Pin].ForceInput(true);

        var data = new byte[] { 0x02, 0x2B, 0x00, 0xD7, 0x04 };
        long lowSince = -1;
        long end = 125_000L * 300;
        bool answered = false;
        while (pico.Cpu.Cycles < end)
        {
            pico.RunCycles(10);
            var g = pico.Gpio[Pin];
            bool hostLow = g.IsOutput && !g.OutputValue;
            if (hostLow && lowSince < 0) lowSince = pico.Cpu.Cycles;
            if (hostLow || lowSince < 0) continue;
            double lowUs = (pico.Cpu.Cycles - lowSince) / 125.0;
            lowSince = -1;
            if (lowUs < 800 || answered) continue;

            answered = true;
            Play(pico, true, 30);
            Play(pico, false, 80);
            Play(pico, true, 80);
            foreach (var b in data)
                for (int i = 7; i >= 0; i--)
                {
                    Play(pico, false, 50);
                    Play(pico, true, ((b >> i) & 1) != 0 ? 70 : 26);
                }
            Play(pico, false, 50);
            Play(pico, true, 10);
        }

        return pico.Uart0.Lines.Where(l => int.TryParse(l.Trim(), out _)).Select(l => int.Parse(l.Trim())).ToList();
    }

    [Test]
    public void ASensorsAnswerIsHeardPulseByPulse()
    {
        var got = RunAgainstASensor();

        // The printed values, in order: len, pulses[0..3], popleft(), len after it, len after clear.
        // The first edge after the trigger only sets the reference, so the sensor's 80 us low
        // is the first interval stored; then 80 high, then a low and a high per bit.
        got.Should().HaveCountGreaterThan(5);
        got[0].Should().BeInRange(80, 84, "len(pulses): a DHT22 answers with 82 intervals once the reference edge is taken");
        got[1].Should().BeInRange(76, 81, "the sensor's 80 us low");
        got[2].Should().BeInRange(76, 81, "its 80 us high");
        got[3].Should().BeInRange(46, 51, "the first bit's 50 us low");
        got[4].Should().BeInRange(22, 27, "a zero bit: 26 us high");
        got[5].Should().Be(got[1], "popleft() returns the oldest, the one pulses[0] was read from");
        got[6].Should().Be(got[0] - 1, "and takes it out");
        got[7].Should().Be(0, "clear() forgets the rest");
    }
}
