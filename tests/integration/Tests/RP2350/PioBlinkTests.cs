using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;

namespace PyMCU.IntegrationTests.Tests.RP2350;

[TestFixture]
public class PioBlinkTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("pio-blink-rp2350");

    [Test]
    public void StateMachine_TogglesPinAutonomously()
    {
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(_firmware);
        var la = sim.AddLogicAnalyzer(25);

        sim.RunMilliseconds(5);

        la.HasToggled(25).Should().BeTrue("PIO drives GP25 with no CPU involvement");
        la.TransitionCount(25).Should().BeGreaterThanOrEqualTo(2);
        sim.HardFaultCount.Should().Be(0);
    }
}
