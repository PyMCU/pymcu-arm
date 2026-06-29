using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.TestKit.Probes;

namespace PyMCU.IntegrationTests.Tests.RP2350;

[TestFixture]
public class SpiTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("spi-rp2350");

    [Test]
    public void Master_ClocksOutBytes()
    {
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(_firmware)
            .AddSpiProbe(0, out var spi);

        sim.RunMilliseconds(5);

        spi.Mosi.Bytes.Should().Contain(new byte[] { 0xAB, 0xCD, 0xEF },
            "the firmware transfers 0xAB, 0xCD, 0xEF on SPI0");
        sim.HardFaultCount.Should().Be(0);
        sim.IsLockedUp.Should().BeFalse();
    }
}
