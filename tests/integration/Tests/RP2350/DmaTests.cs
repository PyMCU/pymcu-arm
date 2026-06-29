using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;

namespace PyMCU.IntegrationTests.Tests.RP2350;

[TestFixture]
public class DmaTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("dma-rp2350");

    [Test]
    public void Copies_WordBetweenMemory()
    {
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(_firmware);

        sim.RunMilliseconds(5);

        // The firmware DMAs 0xDEADBEEF and lights GP25 only if the copy arrived.
        sim.Machine.Sio.GetGpioOut(25).Should().BeTrue("the DMA copied the word to the destination");
        sim.HardFaultCount.Should().Be(0);
        sim.IsLockedUp.Should().BeFalse();
    }
}
