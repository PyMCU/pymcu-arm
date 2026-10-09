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
/// CPython afterward, and a collection having actually run is confirmed by sampling the
/// bump allocator's own write pointer (@__pymcu_gc_heap_top, already in EmitGcRuntime
/// for __pymcu_gc_alloc's own use) while the garbage loop runs: no counter or other new
/// runtime code is needed -- a real compaction makes that pointer drop, which a lazy
/// allocator that only grows (and would have failed outright, long before reaching this
/// test's assertions) cannot otherwise produce. Same technique as
/// AVR/GcCollectForcedUnderLivePressureTests.cs (pymcu-avr).
///
/// The garbage loop's size (200 iterations of a 2000-byte throwaway list) is tuned for
/// RP2040's far larger heap (hundreds of KiB, vs. ATmega328P's ~1.7-1.9 KiB) -- see the
/// AVR test for the same scenario there, with stress parameters scaled down instead; an
/// identical byte count across both targets would either never collect on ARM or take
/// unreasonably long to simulate on AVR.
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

    // @__pymcu_gc_heap_top and @__heap_start: this exact program's build places them at
    // SRAM 0x2000002c and 0x20000060 (confirmed with arm-none-eabi-nm against a built
    // firmware.elf; re-derive if this source text ever changes, since the addresses
    // depend on the program's own global layout).
    private const uint HeapTopAddress = 0x2000002c;
    private const uint HeapStartAddress = 0x20000060;

    [Test]
    public void FlatAndNestedListsSurviveARealCollection_AndTheHeapPointerProvesItRan()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        // Sample the bump pointer every 0.01 ms of simulated time while the garbage
        // loop runs. Measured directly for this exact program: the loop (200 x 2000 B)
        // finishes within about 0.15 ms of simulated time on this chip's much faster
        // clock than AVR's, during which the pointer rises (ordinary bump allocation),
        // then drops sharply (a real mark-and-compact collection reclaiming the loop's
        // dead objects) before settling. 2 ms is a generous multiple of that, so a
        // future change to the codegen or the loop shifting the exact timing still
        // samples through the whole garbage phase.
        uint peak = 0;
        uint biggestDrop = 0;
        for (int step = 0; step < 200; step++)
        {
            pico.RunMilliseconds(0.01);
            uint heapTop = pico.Rp2040.Bus.ReadWord(HeapTopAddress) - HeapStartAddress;
            if (heapTop > peak)
            {
                peak = heapTop;
            }
            else if (peak - heapTop > biggestDrop)
            {
                biggestDrop = peak - heapTop;
            }
        }

        // A lazy bump allocator only grows between collections -- it falls back to
        // gc_collect() exactly when it cannot otherwise satisfy a request, and nothing
        // else in this runtime ever moves heap_top backward. Finding a drop bigger than
        // a single allocation's worth of bytes (2000 B here) is therefore only possible
        // if a real compaction ran and reclaimed the loop's garbage -- not an allocation
        // that merely happened to succeed without ever needing one.
        biggestDrop.Should().BeGreaterThan(2000,
            "the garbage loop (200 x 2000 B) must have forced gc_alloc to collect at "
            + $"least once -- sampled over 2 ms at 0.01 ms steps, the heap pointer "
            + "never dropped by more than a single allocation's worth, which means the "
            + "stress never actually exercised a real compaction");

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
    }
}
