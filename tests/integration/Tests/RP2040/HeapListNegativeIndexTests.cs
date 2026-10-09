using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// GC heap port to ARM, oracle probe 514: a list[T] element lives at
/// base + 2 + index * size, so a negative index (read, store, and augmented-assign)
/// must first resolve against the list's own run-time length (the count byte at
/// base + 0) -- EmitElemAddr's negative-constant-index branch, which this port reused
/// unchanged from the shared generator, widened only at the pointer arithmetic itself
/// (GcPtrWidth).
/// </summary>
[TestFixture]
public class HeapListNegativeIndexTests
{
    [Test]
    public void NegativeIndexReadStoreAndAugAssign_MatchCPython()
    {
        // Oracle probe 514_negative_index_into_a_heap_list.py, GPIOR0's import swapped
        // for an equivalent scratch RAM ptr -- the same adaptation tests/oracle/sweep.py
        // makes for this probe. The scratch byte boots to 0, so g0 == 0 here too.
        var firmware = PymcuCompiler.BuildSourceRp2040(
            "from pymcu.types import ptr, uint8, uint16, int32\n" +
            "GPIOR0: ptr[uint8] = ptr(0x20041FF0)\n" +
            "g0 = GPIOR0.value\n" +
            "xs: list[uint8] = []\n" +
            "xs.append(g0 + 5)\n" +
            "xs.append(g0 + 6)\n" +
            "xs.append(g0 + 7)\n" +
            "ys: list[uint16] = []\n" +
            "ys.append(g0 + 1000)\n" +
            "ys.append(g0 + 2000)\n" +
            "xs[-1] += 10\n" +
            "ys[-2] = g0 + 3000\n" +
            "print(xs[-1], xs[-3], ys[-1], ys[-2], ys[0])\n" +
            "print(\"END\")\n" +
            "while True:\n" +
            "    pass\n");

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue();

        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        // g0 == 0: xs = [5, 6, 7] -> xs[-1] += 10 -> [5, 6, 17]; ys = [1000, 2000] ->
        // ys[-2] = 3000 (index 0) -> [3000, 2000]. Verified against CPython directly.
        sb.ToString().Should().Be("17 5 2000 3000 3000\nEND\n");
    }
}
