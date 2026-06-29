using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class I2cTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("i2c-rp2040");

    [Test]
    public void Master_WritesToDevice()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);

        var writes = new List<byte>();
        pico.Rp2040.I2c0.OnWrite = (addr, data) => { if (addr == 0x3C) writes.Add(data); };

        pico.RunMilliseconds(5);

        writes.Should().Contain(new byte[] { 0x12, 0x34 },
            "the firmware writes 0x12 and 0x34 to the device at 0x3C");
    }
}
