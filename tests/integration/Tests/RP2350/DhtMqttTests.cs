using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.Wireless.Cyw43;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// The whole thing, end to end on a Pico 2 W: read a DHT11 with the portable MicroPython
/// driver and PUBLISH the temperature to an MQTT broker over WiFi (CYW43439 gSPI ->
/// join -> TCP -> MQTT), while a heartbeat LED blinks. The emulator supplies the AP + a
/// built-in MQTT broker; with no real sensor wired the reading is 0, so the payload is
/// "000" -- the point is the DHT->WiFi->MQTT chain runs for real.
/// </summary>
[TestFixture]
public class DhtMqttTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("dht-mqtt-rp2350");

    [Test]
    public void PublishesDhtReadingToBrokerOverWifi()
    {
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm).WithBinary(_firmware);
        sim.Machine.Pio0.ReadGpioIn = () => sim.Machine.IoBank0.GetInputWord();
        sim.Machine.Pio1.ReadGpioIn = () => sim.Machine.IoBank0.GetInputWord();
        sim.Machine.Pio2.ReadGpioIn = () => sim.Machine.IoBank0.GetInputWord();
        sim.Machine.Sio.OnGpioChanged += () => sim.Machine.IoBank0.NotifyPads(0xFFFFFFFFu);

        var dev = new Cyw43439Device(sim.Machine.IoBank0);
        dev.Sdpcm.VisibleAps.Add(new Sdpcm.VirtualAp(
            "RP2350Sharp-AP", new byte[] { 2, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE }, 6, -42, false));
        var net = new VirtualNet(dev.Sdpcm);
        net.EnableMqttBroker(1883);

        string? joined = null;
        dev.Sdpcm.OnStaJoin += s => joined = s;
        for (int i = 0; i < 300 && joined == null; i++) sim.RunMilliseconds(1);
        for (int i = 0; i < 200 && net.MqttPublishes.Count == 0; i++) sim.RunMilliseconds(1);

        joined.Should().Be("RP2350Sharp-AP", "the firmware must join the AP");
        net.MqttPublishes.Should().NotBeEmpty("the DHT reading must be published to the broker");
        net.MqttPublishes[0].Topic.Should().Be("dht");
        Encoding.ASCII.GetString(net.MqttPublishes[0].Payload).Should().HaveLength(3,
            "the payload is the reading as 3 ASCII digits");

        // The LED still blinks (the async heartbeat task runs alongside).
        bool low = false, high = false;
        for (int i = 0; i < 12; i++)
        {
            sim.RunMilliseconds(100);
            if (sim.Machine.Sio.GetGpioOut(25)) high = true; else low = true;
        }
        high.Should().BeTrue(); low.Should().BeTrue();
        sim.HardFaultCount.Should().Be(0);
    }
}
