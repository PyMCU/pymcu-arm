using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;

namespace PyMCU.IntegrationTests.Tests.RP2350;

[TestFixture]
public class AdcTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("adc-rp2350");

    [Test]
    public void Read_TracksInputVoltage()
    {
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(_firmware);

        // High input on ADC0 (GP26) -> conversion above mid-scale -> LED on.
        sim.SetAdcGpioVoltage(26, 3.3);
        sim.RunMilliseconds(2);
        sim.Machine.Sio.GetGpioOut(25).Should().BeTrue("3.3 V reads above mid-scale");

        // Low input -> LED off.
        sim.SetAdcGpioVoltage(26, 0.0);
        sim.RunMilliseconds(2);
        sim.Machine.Sio.GetGpioOut(25).Should().BeFalse("0 V reads below mid-scale");

        sim.HardFaultCount.Should().Be(0);
    }
}
