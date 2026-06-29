using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;

namespace PyMCU.IntegrationTests.Tests.RP2350;

[TestFixture]
public class BlinkTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("blink-rp2350");

    private static RP2350TestSimulation Sim()
        => RP2350TestSimulation.Create(CpuArchitecture.Arm).WithBinary(_firmware);

    [Test]
    public void Boots_WithoutHardFaultOrLockup()
    {
        using var sim = Sim();
        // A bad picobin block / vector table would leave the core stuck in the
        // BootROM or fault immediately; these are the cheapest boot smoke checks.
        sim.RunMilliseconds(5);
        sim.HardFaultCount.Should().Be(0);
        sim.IsLockedUp.Should().BeFalse();
    }

    [Test]
    public void Led_StartsHighAfterBoot()
    {
        using var sim = Sim();
        // main() configures GP25 as output and drives it high before the first
        // 500 ms delay elapses.
        sim.RunMilliseconds(5);
        sim.Machine.Sio.GetGpioOutputEnable(25).Should().BeTrue("GP25 must be an output");
        sim.Machine.Sio.GetGpioOut(25).Should().BeTrue("GP25 must be driven high first");
    }

    [Test]
    public void Led_TogglesOverTime()
    {
        using var sim = Sim();
        bool sawHigh = false;
        bool sawLow = false;

        // Sample the LED across more than one full blink period (2 x 500 ms).
        for (int i = 0; i < 25; i++)
        {
            sim.RunMilliseconds(100);
            if (sim.Machine.Sio.GetGpioOut(25)) sawHigh = true;
            else sawLow = true;
        }

        sawHigh.Should().BeTrue("the LED must be driven high at some point");
        sawLow.Should().BeTrue("the LED must be driven low at some point");
        sim.HardFaultCount.Should().Be(0);
    }
}
