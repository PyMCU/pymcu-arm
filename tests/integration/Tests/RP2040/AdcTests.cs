using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class AdcTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("adc-rp2040");

    [Test]
    public void Read_TracksInputVoltage()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);

        // High conversion on ADC0 -> above mid-scale -> LED on.
        pico.Rp2040.Adc.ReadChannel = _ => 4000;
        pico.RunMilliseconds(2);
        pico.Gpio[25].Should().BeHigh("a 4000 reading is above mid-scale");

        // Low conversion -> LED off.
        pico.Rp2040.Adc.ReadChannel = _ => 0;
        pico.RunMilliseconds(2);
        pico.Gpio[25].Should().BeLow("a 0 reading is below mid-scale");
    }
}
