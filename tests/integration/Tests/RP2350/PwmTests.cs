using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;

namespace PyMCU.IntegrationTests.Tests.RP2350;

[TestFixture]
public class PwmTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("pwm-rp2350");

    [Test]
    public void Drives_SquareWaveOnPin()
    {
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(_firmware);
        var la = sim.AddLogicAnalyzer(2);

        sim.RunMilliseconds(5);

        la.HasToggled(2).Should().BeTrue("GP2 is driven by a 2 kHz PWM");
        // ~2 kHz over 5 ms ≈ 10 periods → at least a few edges either way.
        la.TransitionCount(2).Should().BeGreaterThanOrEqualTo(4);
        sim.HardFaultCount.Should().Be(0);
    }
}
