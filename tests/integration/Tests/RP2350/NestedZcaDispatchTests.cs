using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// Nested-ZCA method dispatch: a class that stores a machine.Pin in a field and
/// calls methods on it (pin._pin -> hal.Pin) -- through inheritance and from a
/// module-level instance, the exact shape of the MicroPython DHT driver. If the
/// nested dispatch were broken (the field's class lost after the ZCA collapse),
/// the firmware would either fail to compile or emit no MMIO and GP25 would never
/// move. These checks prove the dispatch produced real stores on real M33 silicon.
/// </summary>
[TestFixture]
public class NestedZcaDispatchTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("nested-zca-rp2350");

    private static RP2350TestSimulation Sim()
        => RP2350TestSimulation.Create(CpuArchitecture.Arm).WithBinary(_firmware);

    [Test]
    public void Boots_WithoutHardFaultOrLockup()
    {
        using var sim = Sim();
        sim.RunMilliseconds(5);
        sim.HardFaultCount.Should().Be(0);
        sim.IsLockedUp.Should().BeFalse();
    }

    [Test]
    public void Led_IsConfiguredAsOutput()
    {
        using var sim = Sim();
        // The module-level `Pin(25, Pin.OUT)` plus the inherited `turn_on()` path
        // must have configured GP25 as an output via the nested dispatch.
        sim.RunMilliseconds(5);
        sim.Machine.Sio.GetGpioOutputEnable(25).Should().BeTrue("GP25 must be an output");
    }

    [Test]
    public void Led_TogglesOverTime_ViaNestedDispatch()
    {
        using var sim = Sim();
        bool sawHigh = false;
        bool sawLow = false;

        // turn_on()/turn_off() reach hal.Pin.high()/.low() through machine.Pin --
        // two levels of ZCA nesting from a module-level, inherited field. Sample
        // across more than one full blink period (2 x 300 ms).
        for (int i = 0; i < 16; i++)
        {
            sim.RunMilliseconds(100);
            if (sim.Machine.Sio.GetGpioOut(25)) sawHigh = true;
            else sawLow = true;
        }

        sawHigh.Should().BeTrue("turn_on() -> self._pin.high() must drive GP25 high");
        sawLow.Should().BeTrue("turn_off() -> self._pin.low() must drive GP25 low");
        sim.HardFaultCount.Should().Be(0);
    }
}
