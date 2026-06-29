using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class PwmTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("pwm-rp2040");

    [Test]
    public void Configures_SliceDuty()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        pico.RunMilliseconds(2);

        // GP2 -> slice 1, channel A. 50% of TOP (125 MHz / 5 kHz = 25000) = 12500.
        pico.Rp2040.Pwm.GetDutyA(1).Should().Be(12500,
            "the HAL programs the channel-A compare for a 50% duty");
    }
}
