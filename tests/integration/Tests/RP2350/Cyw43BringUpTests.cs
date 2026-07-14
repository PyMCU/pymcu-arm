using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.Wireless.Cyw43;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// Validates the PyMCU CYW43439 WiFi HAL's gSPI bus bring-up against the emulator's
/// chip model -- the same seam the RPI_PICO2_W MicroPython firmware drives. The HAL
/// bit-bangs gSPI on GP23/24/25/29 (no PIO), so we wire the CYW43439Device to the
/// pads and assert the bring-up reaches "chip alive": power on, read the test
/// register (F0 @0x14 -> 0xFEEDBEAD), switch the bus to 32-bit (F0 @0x00), and cross
/// into F1 backplane access. Bring-up only; WLAN join / TCP / MQTT are staged follow-ups.
/// </summary>
[TestFixture]
public class Cyw43BringUpTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2350("wifi-cyw43-rp2350");

    [Test]
    public void GspiBringUp_ReachesChipAliveAndBackplane()
    {
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm).WithBinary(_firmware);

        sim.Machine.Pio0.ReadGpioIn = () => sim.Machine.IoBank0.GetInputWord();
        sim.Machine.Pio1.ReadGpioIn = () => sim.Machine.IoBank0.GetInputWord();
        sim.Machine.Pio2.ReadGpioIn = () => sim.Machine.IoBank0.GetInputWord();
        sim.Machine.Sio.OnGpioChanged += () => sim.Machine.IoBank0.NotifyPads(0xFFFFFFFFu);

        var dev = new Cyw43439Device(sim.Machine.IoBank0);
        dev.Sdpcm.VisibleAps.Add(new Sdpcm.VirtualAp(
            "RP2350Sharp-AP", new byte[] { 0x02, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE }, Channel: 6, Rssi: -42, Secured: false));
        bool sawTestRead = false, sawBusControlWrite = false, sawBackplane = false;
        int f2reads = 0;
        string? joined = null;
        dev.OnCommand += (w, fn, addr, sz) =>
        {
            if (!w && fn == 0 && addr == 0x14) sawTestRead = true;
            if (w && fn == 0 && addr == 0x00 && !dev.Word32) sawBusControlWrite = true;
            if (fn == 1) sawBackplane = true;
            if (!w && fn == 2) f2reads++;
        };
        dev.Sdpcm.OnStaJoin += s => joined = s;

        for (int i = 0; i < 200 && joined == null; i++)
            sim.RunMilliseconds(1);
        for (int i = 0; i < 30; i++) sim.RunMilliseconds(1);  // let the RX reads drain the async events

        dev.Powered.Should().BeTrue("WL_REG_ON must power the chip");
        sawTestRead.Should().BeTrue("the HAL reads SPI_READ_TEST_REGISTER (F0 @0x14)");
        sawBusControlWrite.Should().BeTrue("the HAL writes SPI_BUS_CONTROL (F0 @0x00) in swapped mode");
        dev.Word32.Should().BeTrue("the bus switches to 32-bit little-endian after SPI_BUS_CONTROL");
        sawBackplane.Should().BeTrue("the HAL proceeds into F1 backplane access");
        joined.Should().Be("RP2350Sharp-AP",
            "join_open must send a WLC_SET_SSID ioctl over SDPCM and associate with the visible AP");
        f2reads.Should().BeGreaterThan(0,
            "the HAL must read the post-join async SDPCM events over F2 (RX path)");
        sim.HardFaultCount.Should().Be(0);
    }
}
