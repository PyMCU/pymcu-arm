using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.Wireless.Cyw43;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// The DHT -> WiFi -> MQTT chain written in the MicroPython-compat flavor
/// (network.WLAN + umqtt.simple.MQTTClient) -- the same source shape that runs under
/// MicroPython on a Pico 2 W. Proves the flavor wrappers drive the real CYW43439 stack.
/// </summary>
[TestFixture]
public class DhtMqttFlavorTests
{
    private static byte[] Publish(string example)
    {
        // helper: build + run + return the first MQTT payload the broker received
        return PymcuCompiler.BuildRp2350(example);
    }

    [Test]
    public void MicroPythonFlavor_PublishesToBroker()
    {
        var fw = PymcuCompiler.BuildRp2350("dht-mqtt-mp-rp2350");
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm).WithBinary(fw);
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

        joined.Should().Be("RP2350Sharp-AP");
        net.MqttPublishes.Should().NotBeEmpty("the umqtt client must publish over WiFi");
        net.MqttPublishes[0].Topic.Should().Be("dht");
        sim.HardFaultCount.Should().Be(0);
    }

    [Test]
    public void CircuitPythonFlavor_PublishesToBroker()
    {
        var fw = PymcuCompiler.BuildRp2350("dht-mqtt-cp-rp2350");
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm).WithBinary(fw);
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
        joined.Should().Be("RP2350Sharp-AP");
        net.MqttPublishes.Should().NotBeEmpty("the adafruit_minimqtt client must publish over WiFi");
        net.MqttPublishes[0].Topic.Should().Be("dht");
        sim.HardFaultCount.Should().Be(0);
    }
}
