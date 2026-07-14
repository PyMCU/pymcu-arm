using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// Module-level init: an instance constructed at module scope (the shape of the
/// MicroPython DHT driver's `sensor = DHT11(Pin(...))`) must have its construction
/// MMIO actually run at startup -- mirroring Python, where the module body executes
/// before the entry point. Before the fix, only a Pin constructed *inside* main()
/// configured its hardware; a module-level one compiled but never ran. These checks
/// prove the module-level construction ran AND its nested Pin field works from main.
/// </summary>
[TestFixture]
public class ModuleInitTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("module-init-rp2350");

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
    public void ModuleLevelConstruction_RanAtStartup()
    {
        using var sim = Sim();
        // The module-level `Blinker(Pin(25, Pin.OUT))` must have run its construction
        // before main(); if module-level init were dropped, GP25 would never be an
        // output.
        sim.RunMilliseconds(5);
        sim.Machine.Sio.GetGpioOutputEnable(25).Should().BeTrue(
            "module-level Pin(25, Pin.OUT) construction must run at startup");
    }

    [Test]
    public void Led_TogglesOverTime_FromModuleLevelInstance()
    {
        using var sim = Sim();
        bool sawHigh = false;
        bool sawLow = false;

        // dev.tick() reaches hal.Pin.high()/.low() through the nested machine.Pin of a
        // MODULE-LEVEL instance. Sample across more than one full blink period (2 x 200 ms).
        for (int i = 0; i < 12; i++)
        {
            sim.RunMilliseconds(100);
            if (sim.Machine.Sio.GetGpioOut(25)) sawHigh = true;
            else sawLow = true;
        }

        sawHigh.Should().BeTrue("dev.tick(1) -> self._pin.high() must drive GP25 high");
        sawLow.Should().BeTrue("dev.tick(0) -> self._pin.low() must drive GP25 low");
        sim.HardFaultCount.Should().Be(0);
    }
}
