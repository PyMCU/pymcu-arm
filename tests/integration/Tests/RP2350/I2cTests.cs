using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.TestKit.Probes;

namespace PyMCU.IntegrationTests.Tests.RP2350;

[TestFixture]
public class I2cTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("i2c-rp2350");

    [Test]
    public void Master_WritesToDevice()
    {
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(_firmware)
            .AddI2cProbe(0, out var i2c);

        sim.RunMilliseconds(5);

        var written = i2c.ForAddress(0x3C).SelectMany(t => t.Written).ToList();
        written.Should().Contain(new byte[] { 0x12, 0x34 },
            "the firmware writes 0x12 and 0x34 to the device at 0x3C");
        sim.HardFaultCount.Should().Be(0);
        sim.IsLockedUp.Should().BeFalse();
    }
}
