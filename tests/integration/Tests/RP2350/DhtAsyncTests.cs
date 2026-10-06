using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.TestKit.Probes;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// Hardcore end-to-end demo: the portable MicroPython DHT11 driver driven by THREE
/// cooperative async tasks (blink GP25, sample the DHT on GP2, report over UART0).
/// It exercises everything at once on real M33 silicon -- nested-ZCA dispatch through
/// the DHT's inherited machine.Pin field, module-level instance init (led/uart/sensor),
/// the async state-machine transform, and UART decimal output (uart.print_byte).
///
/// There is no DHT11 wired to the emulator, so sample() times out and report() prints
/// the failure path -- which is exactly what proves all three tasks run end to end:
/// the LED still blinks, the DHT read still executes (and times out cleanly), and the
/// UART still emits. The real T=/H= values are validated on hardware via a logic
/// analyzer; here we assert the firmware boots, blinks, and talks.
/// </summary>
[TestFixture]
public class DhtAsyncTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("dht-async-rp2350");

    private static RP2350TestSimulation Sim(out UartProbe uart)
        => RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(_firmware)
            .AddUartProbe(0, out uart);

    [Test]
    public void Boots_WithoutHardFaultOrLockup()
    {
        using var sim = Sim(out _);
        sim.RunMilliseconds(10);
        sim.HardFaultCount.Should().Be(0);
        sim.IsLockedUp.Should().BeFalse();
    }

    [Test]
    public void BlinkTask_TogglesLed()
    {
        using var sim = Sim(out _);
        bool sawHigh = false, sawLow = false;
        // blink() toggles GP25 every 250 ms; sample more than one period.
        for (int i = 0; i < 12; i++)
        {
            sim.RunMilliseconds(100);
            if (sim.Machine.Sio.GetGpioOut(25)) sawHigh = true;
            else sawLow = true;
        }
        sawHigh.Should().BeTrue("the heartbeat task must drive GP25 high");
        sawLow.Should().BeTrue("the heartbeat task must drive GP25 low");
    }

    [Test]
    public void ReportTask_EmitsTemperatureAndHumidityOverUart()
    {
        using var sim = Sim(out var uart);
        // The single-wire protocol needs a peer: on a dead pin every time_pulse_us
        // times out and report() prints the "DHT FAIL" branch, so without stimulus
        // no "T=" can ever reach the wire. Answer each measure() like a DHT11 does
        // -- after the firmware's 18 ms start pulse releases GP2, send the ACK
        // (80 us low, 80 us high) and 40 zero bits (50 us low + ~26 us high). The
        // all-zero frame passes the driver's checksum, so report() prints "T=0\nH=0\n".
        // The exact values don't matter -- the point is that the full chain runs end
        // to end: the start pulse, both ACK waits, five _read_byte and the checksum.
        for (int attempt = 0; attempt < 4 && !uart.Contains("T="); attempt++)
            AnswerDht11(sim);
        sim.WaitForUart(uart, "T=", timeoutMs: 20_000).ConditionMet
            .Should().BeTrue("report() must emit a temperature line over UART0");
        sim.WaitForUart(uart, "H=", timeoutMs: 20_000).ConditionMet
            .Should().BeTrue("report() must emit a humidity line over UART0");
        sim.HardFaultCount.Should().Be(0);
    }

    private const int DhtPin = 2;

    // Drives one all-zeros DHT11 answer on GP2: waits for the firmware's start
    // pulse (GP2 driven low ~18 ms, then released to input) and answers within
    // the driver's 1 ms time_pulse_us window.
    private static void AnswerDht11(RP2350TestSimulation sim)
    {
        var sio = sim.Machine.Sio;
        // sample() runs measure() every ~2 s; poll the OE bit every 20 ms so a
        // start pulse is always caught.
        for (int i = 0; i < 150 && !(sio.GetGpioOutputEnable(DhtPin) && !sio.GetGpioOut(DhtPin)); i++)
            sim.RunMilliseconds(20);
        // End of the start pulse: poll at 10 us so the answer lands well inside
        // the driver's first 1 ms wait.
        for (int i = 0; i < 200 && sio.GetGpioOutputEnable(DhtPin); i++)
            sim.RunMicroseconds(10);
        void Low(double us)  { sim.SetGpioInput(DhtPin, false); sim.RunMicroseconds(us); }
        void High(double us) { sim.SetGpioInput(DhtPin, true);  sim.RunMicroseconds(us); }
        Low(80); High(80);                               // ACK
        for (int bit = 0; bit < 40; bit++) { Low(50); High(26); }
        High(40);                                        // line idles high
    }
}
