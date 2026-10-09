using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Forces a real mark-and-compact collection (not just an allocation that happens to
/// succeed without ever needing one) while several live, aliased objects -- flat and
/// nested (ref-bearing) -- are reachable only through the shadow stack and, for the
/// inner lists, only through the ref-tracing pass (__pymcu_gc_trace_refs,
/// Rp2040LlvmCodeGen.EmitGcRuntime). Every element and every alias is checked against
/// CPython afterward, and @__pymcu_gc_collect_count (bumped once per gc_collect call,
/// zero-initialised by ordinary .bss clearing -- unlike AVR, nothing extra was needed
/// for that) is read directly from SRAM (RP2040Machine.Bus.ReadWord) to confirm a
/// collection actually ran -- the test does not infer this from the program's own
/// output alone.
///
/// The garbage loop's size (200 iterations of a 2000-byte throwaway list) is tuned for
/// RP2040's far larger heap (hundreds of KiB, vs. ATmega328P's ~1.7-1.9 KiB) -- see
/// AVR/GcCollectForcedUnderLivePressureTests.cs for the same scenario there, with
/// stress parameters scaled down instead; an identical byte count across both targets
/// would either never collect on ARM or take unreasonably long to simulate on AVR.
/// </summary>
[TestFixture]
public class GcCollectForcedUnderLivePressureTests
{
    // Same live-object structure as the AVR test: a flat list, a nested one with an
    // inner-list alias and a whole-list alias, a function reading through a list
    // parameter, then a garbage loop sized to force a real collection on this chip,
    // then one more live object allocated after the garbage.
    private const string Source =
        "from pymcu.types import uint8, uint16\n\n" +
        "flat: list[uint8] = []\n" +
        "flat.append(10)\n" +
        "flat.append(20)\n" +
        "flat.append(30)\n\n" +
        "nested: list[list[uint8]] = [[1, 2], [3, 4]]\n" +
        "alias_a = nested[0]\n" +
        "alias_b = nested\n\n\n" +
        "def read_flat(xs: list[uint8]) -> uint8:\n" +
        "    return xs[1]\n\n\n" +
        "i: uint16 = 0\n" +
        "while i < 200:\n" +
        "    junk: list[uint8] = list(2000)\n" +
        "    i = i + 1\n\n" +
        "tail: list[uint8] = []\n" +
        "tail.append(99)\n\n" +
        "print(flat[0], flat[1], flat[2])\n" +
        "print(nested[0][0], nested[0][1], nested[1][0], nested[1][1])\n" +
        "print(alias_a[0], alias_a[1])\n" +
        "alias_b[1][1] = 77\n" +
        "print(nested[1][1])\n" +
        "print(read_flat(flat))\n" +
        "print(tail[0])\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    // @__pymcu_gc_collect_count: this exact program's build places it at SRAM
    // 0x20000060 (confirmed with arm-none-eabi-nm against a built firmware.elf;
    // re-derive if this source text ever changes, since the address depends on the
    // program's own global layout).
    private const uint CollectCountAddress = 0x20000060;

    [Test]
    public void FlatAndNestedListsSurviveARealCollection_AndTheCounterProvesItRan()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue();

        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        // Verified directly against CPython running the same live-object sequence
        // (the garbage loop has no observable effect on it): 10 20 30; nested unwraps
        // to 1 2 3 4; alias_a (== nested[0]) reads 1 2; alias_b[1][1] = 77 is visible
        // through nested itself; read_flat(flat) is flat[1] == 20; tail[0] == 99.
        sb.ToString().Should().Be("10 20 30\n1 2 3 4\n1 2\n77\n20\n99\nEND\n",
            "every live object -- flat, nested, both its aliases, and the post-garbage "
            + "tail -- must read back correctly after a real compaction");

        uint count = pico.Rp2040.Bus.ReadWord(CollectCountAddress);
        count.Should().BeGreaterThan(0,
            "the garbage loop (200 x 2000 B) must have forced gc_alloc to collect at "
            + "least once for the live objects above to have survived anything -- a 0 "
            + "here means the stress never actually exercised a real compaction");
    }
}
