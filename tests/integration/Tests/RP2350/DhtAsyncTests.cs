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
        // With no DHT11 wired to the emulator the sampled frame reads all-zero (a
        // self-consistent 0/0 reading whose checksum validates), so report() prints
        // "T=0\nH=0\n". The exact values don't matter here -- the point is that the
        // full chain runs end to end: sample() executes the portable DHT protocol
        // (mode flips, time_pulse_us, five _read_byte, checksum) and report() formats
        // the result with uart.print_byte over UART0. Real T/H values are validated on
        // hardware with a logic analyzer.
        sim.WaitForUart(uart, "T=", timeoutMs: 20_000).ConditionMet
            .Should().BeTrue("report() must emit a temperature line over UART0");
        sim.WaitForUart(uart, "H=", timeoutMs: 20_000).ConditionMet
            .Should().BeTrue("report() must emit a humidity line over UART0");
        sim.HardFaultCount.Should().Be(0);
    }
}
