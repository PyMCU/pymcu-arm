using System;
using System.IO;
using FluentAssertions;
using NUnit.Framework;
using RP2040Sharp.Wireless.Cyw43;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// The Pico W half of the CYW43439 bring-up, and the reason it is a separate fixture rather
/// than a parameter on the RP2350 one: it has to build a DIFFERENT firmware from the SAME
/// source, and that is the claim under test.
///
/// One driver serves both boards (pymcu.hal.rp.cyw43), selecting twelve MCU registers by chip
/// and sharing the CYW43439's own register map. Ten of the twelve differ between the parts,
/// and one pair is worth knowing about: SIO_GPIO_OUT_CLR on the RP2040 and SIO_GPIO_OUT_SET on
/// the RP2350 are both 0xD0000018. A firmware built with the wrong map does not fault and does
/// not touch a register that is not there; it SETS where it meant to CLEAR, which on the
/// bit-banged clock line is a bus that never ticks. Nothing but running it says otherwise,
/// which is what this fixture is for.
///
/// WHAT A GREEN HERE DOES NOT MEAN, and it is written here because the name of the fixture
/// suggests otherwise. It is evidence about the PROTOCOL, not about TIMING.
///
/// The emulated CYW43439 is edge-driven and rate-agnostic: GSpiSlave reacts to pad
/// transitions in OnRising/OnFalling, counts bits and presents the next one, and there is no
/// clock source, no elapsed time, no Hz and no setup/hold anywhere in RP2040Sharp.Wireless.
/// It checks the SEQUENCE of edges, never their spacing.
///
/// And the spacing really does differ between the two boards. The bring-up path this test
/// measures contains no delay and never reads __FREQ__, so its gSPI clock is set entirely by
/// the instruction stream. (The driver as a whole is no longer delay-free: join_wpa2 waits
/// 2 ms before WLC_SET_WSEC_PMK, which the radio firmware needs before it will take the PMK.
/// That is in the JOIN path and nothing here reaches it, so the figures below still stand.)
/// 125 MHz on this part against 150 on the RP2350, on an M0+ rather than an M33.
///
/// So the spacing was measured apart from this test, by counting clk_sys cycles between
/// rising edges of WL_CLK over this same bring-up on both boards. Both emulators count real
/// cycles rather than instructions -- RP2040Machine.InstructionCount is a misnamed
/// CortexM0Plus.Cycles, and the M33's CycleAccurate model is on by default -- so the two
/// figures share a unit and no conversion was invented. The bit SEQUENCE came out identical
/// edge for edge, 21 read bursts of lengths 32x15 96x2 224x1 832x3 and 3503 write intervals
/// on each, which is the strongest thing the pair says: one program, one waveform shape.
/// Per bit, in nanoseconds:
///
///                              rp2040    rp2350   ratio
///     F0 read32 data loop mean   51.7      37.3    1.39
///     outer-loop back edge mean 447.4      80.5    5.56
///     whole bring-up bus time  561.7us   321.4us   1.75
///
/// read32's own bit costs the same five cycles on both parts, so the 1.2x on its median is
/// the clock ratio and nothing else. The 5.6x is somewhere else and is deterministic, not
/// jitter: every interval over 20 cycles on the RP2040 falls at index %8 == 0 inside its
/// burst, which is the back edge of the `while pad > 0` loop around _clk_in_bit(), and the
/// RP2040 has none over 20 outside it while the RP2350 has none at all. It matches what the
/// disassembly shows, thumbv6m reaching the SIO addresses through a literal pool where
/// thumbv8m.main materialises them inline.
///
/// The worst single interval anywhere is 447 ns, which stretches the clock LOW phase and
/// costs throughput rather than correctness on a bus the master clocks. Nobody has measured
/// the CYW43439's own limits against either board, and an emulator is not where that gets
/// settled: these numbers bound the two firmwares against each other, not against silicon.
/// </summary>
[TestFixture]
public class Cyw43BringUpTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("wifi-cyw43-rp2040");

    [Test]
    public void TheTwoExamplesAreTheSameProgram()
    {
        // The pair's whole point. If someone edits one and not the other, the Pico W stops
        // being evidence about the shared driver and becomes evidence about a second program.
        // The repo root the way PymcuCompiler finds it. Duplicated in four lines rather than
        // widening its `private static RepoRoot`, because a test wanting a path is a thin
        // reason to change the visibility of something the whole suite depends on.
        var dir = AppContext.BaseDirectory;
        while (dir != null && !(File.Exists(Path.Combine(dir, "hatch_build.py"))
                                && Directory.Exists(Path.Combine(dir, "examples"))))
            dir = Directory.GetParent(dir)?.FullName;
        dir.Should().NotBeNull("the suite runs from inside the pymcu-arm tree");
        var root = Path.Combine(dir!, "examples");
        var w = File.ReadAllBytes(Path.Combine(root, "wifi-cyw43-rp2040", "src", "main.py"));
        var w2 = File.ReadAllBytes(Path.Combine(root, "wifi-cyw43-rp2350", "src", "main.py"));
        w.Should().Equal(w2,
            "the Pico W and Pico 2 W examples must differ only in `target`; a copy adapted per "
            + "board would prove a program exists for each, not that it is the same program");
    }

    [Test]
    public void GspiBringUp_ReachesChipAliveAndJoins()
    {
        using var pico = PicoWSimulation.Create(_firmware);

        // The SSID the shared source joins. It names RP2350Sharp because the Pico 2 W fixture
        // has always offered that, and the two sources have to stay byte-identical.
        pico.OfferAp("RP2350Sharp-AP");

        bool sawTestRead = false, sawBusControlWrite = false, sawBackplane = false;
        int f2reads = 0;
        string? joined = null;
        pico.Radio.OnCommand += (w, fn, addr, sz) =>
        {
            if (!w && fn == 0 && addr == 0x14) sawTestRead = true;
            if (w && fn == 0 && addr == 0x00 && !pico.Radio.Word32) sawBusControlWrite = true;
            if (fn == 1) sawBackplane = true;
            if (!w && fn == 2) f2reads++;
        };
        pico.Wifi.OnStaJoin += s => joined = s;

        for (int i = 0; i < 200_000 && joined == null; i++) pico.Step();
        for (int i = 0; i < 20_000; i++) pico.Step();   // let the RX reads drain the async events

        pico.Radio.Powered.Should().BeTrue("WL_REG_ON must power the chip");
        sawTestRead.Should().BeTrue("the HAL reads SPI_READ_TEST_REGISTER (F0 @0x14)");
        sawBusControlWrite.Should().BeTrue("the HAL writes SPI_BUS_CONTROL (F0 @0x00) in swapped mode");
        pico.Radio.Word32.Should().BeTrue("the bus switches to 32-bit little-endian");
        sawBackplane.Should().BeTrue("the HAL proceeds into F1 backplane access");
        joined.Should().Be("RP2350Sharp-AP",
            "join_open must associate with the visible AP over SDPCM, on the RP2040's register map");
        f2reads.Should().BeGreaterThan(0, "the HAL must read the post-join events over F2");
    }
}
