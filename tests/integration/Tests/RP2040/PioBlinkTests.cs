using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class PioBlinkTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("pio-blink-rp2040");

    [Test]
    public void StateMachine_TogglesPinAutonomously()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);

        // The CPU only sets up the PIO state machine, then spins; GP25 must still
        // toggle because PIO drives it on its own.
        bool sawHigh = false, sawLow = false;
        for (int i = 0; i < 40; i++)
        {
            pico.RunMicroseconds(500);
            if (pico.Gpio[25].DigitalValue) sawHigh = true;
            else sawLow = true;
        }

        sawHigh.Should().BeTrue("the PIO program drives GP25 high");
        sawLow.Should().BeTrue("the PIO program drives GP25 low");
    }
}
