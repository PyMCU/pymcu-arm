using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.Wireless.Cyw43;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// End-to-end WiFi -> MQTT on a Pico 2 W: the firmware brings up the CYW43439, joins the
/// AP, then opens a TCP connection to the emulator's built-in MQTT broker and PUBLISHes a
/// reading. Everything is real -- the guest bit-bangs gSPI, frames Ethernet/IP/TCP over the
/// F2 data channel, and speaks MQTT; the broker (VirtualNet) completes the TCP handshake,
/// answers CONNECT with CONNACK, and records the PUBLISH. Validates the whole stack the DHT
/// demo publishes through.
/// </summary>
[TestFixture]
public class Cyw43MqttTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("wifi-cyw43-rp2350");

    [Test]
    public void PublishesReadingToMqttBroker()
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
        for (int i = 0; i < 150 && net.MqttPublishes.Count == 0; i++) sim.RunMilliseconds(1);

        joined.Should().Be("RP2350Sharp-AP");
        net.MqttPublishes.Should().ContainSingle("the firmware must PUBLISH exactly once");
        net.MqttPublishes[0].Topic.Should().Be("dht");
        Encoding.ASCII.GetString(net.MqttPublishes[0].Payload).Should().Be("042",
            "the published payload is the reading (42) as ASCII");
        sim.HardFaultCount.Should().Be(0);
    }
}
