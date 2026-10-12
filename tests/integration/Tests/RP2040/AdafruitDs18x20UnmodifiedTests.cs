using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Wire-oracle test for the UNMODIFIED Adafruit DS18x20 and OneWire drivers on the Feather
/// RP2040 (fixtures/adafruit-ds18x20-unmodified), with the library's simpletest verbatim as
/// main.py.
///
/// The firmware scans the bus (SEARCH ROM, 64 bit steps), builds a OneWireAddress from the ROM it
/// found, asks the sensor to convert and reads the scratchpad back, so it goes through every
/// feature the example needs from the language: a buffer that is None on the first scan step and
/// a buffer after, a tuple return with an optional buffer slot, a growable list of instances and
/// the address read back out of it. A OneWireSlaveSimulator on GPIO7 (board.D5) answers as a
/// DS18B20 holding 23.5 C.
/// </summary>
[TestFixture]
public class AdafruitDs18x20UnmodifiedTests
{
    private const string Fixture = "adafruit-ds18x20-unmodified";
    private const short Raw = 0x0178;           // 376 / 16 = 23.5 C
    private const int D5 = 7;                   // board.D5 on the Feather RP2040

    // md5 of each vendored file, pinned so an accidental edit fails the test.
    private static readonly (string RelPath, string Md5)[] VendoredFiles =
    {
        ("src/adafruit_ds18x20.py", "69428f19a252deb15cff1ff56ac8d474"),
        ("src/adafruit_onewire/bus.py", "4ad3c651872ab744d8ffb3064785f32c"),
        ("src/adafruit_onewire/device.py", "0092c11d7837994454c35eae9c9375ed"),
    };

    private static readonly byte[] Serial = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };

    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildFixtureRp2040(Fixture);

    [Test]
    public void VendoredFiles_AreByteIdenticalToUpstream()
    {
        var root = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "fixtures", Fixture);
        foreach (var (rel, expectedMd5) in VendoredFiles)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue($"vendored file {rel} must exist in the fixture");
            var actual = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            actual.Should().Be(expectedMd5,
                $"{rel} must stay byte-identical to upstream, or this test no longer measures the unmodified driver");
        }
    }

    [Test]
    public void TheTemperatureTheSensorHoldsIsPrinted()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        var slave = new OneWireSlaveSimulator(pico, pico.Gpio[D5], Serial, Raw);

        var expected = $"Temperature: {Raw / 16.0:0.000}C";
        slave.RunUntil(() => pico.Uart0.Text.Contains("C\n"), 4000)
            .Should().BeTrue($"the firmware prints a reading; serial so far: '{pico.Uart0.Text}'");
        pico.Uart0.Text.Should().Contain(expected);
    }

    [Test]
    public void TheMasterAddressesTheSensorByTheRomItFound()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        var slave = new OneWireSlaveSimulator(pico, pico.Gpio[D5], Serial, Raw);

        slave.RunUntil(() => pico.Uart0.Text.Contains("Temperature:"), 4000).Should().BeTrue();

        // The scan used SEARCH ROM, and every later transaction selected the device with MATCH ROM
        // carrying the eight bytes the scan returned: a ROM copied wrong would fail the match and
        // the sensor would never answer.
        slave.CommandsSeen.Should().StartWith(new byte[] { 0xF0, 0x55 });
        slave.CommandsSeen.Should().Contain(new byte[] { 0x44, 0xBE });
    }
}
