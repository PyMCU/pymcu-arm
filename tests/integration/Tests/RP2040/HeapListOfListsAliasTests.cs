using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// GC heap port to ARM, oracle probe 610: `row = cells[y]` on a list[list[uint8]] binds
/// row to the SAME inner list CPython would (a plain pointer copy of the GC_REF value --
/// no GC-specific work needed for aliasing itself), through a function parameter and a
/// loop that rebinds the name each iteration.
///
/// NOT yet exercised here: a collection actually running while a ref-bearing (list of
/// lists) object is live. This probe's few small allocations never approach exhausting
/// the heap, so gc_collect() never runs during it -- the ref-bearing payload trace pass
/// (marking/fixing up the INNER lists a compacting collection would otherwise treat as
/// unreachable garbage, since the shadow stack only ever names the OUTER list) is a
/// separate, not-yet-implemented piece of this port. Aliasing semantics alone are
/// correct; the collector's handling of nested lists under real compaction pressure
/// still needs its own test once that pass lands.
/// </summary>
[TestFixture]
public class HeapListOfListsAliasTests
{
    [Test]
    public void RowAliasThroughAFunctionAndALoop_MatchesCPython()
    {
        // Oracle probe 610_heap_row_alias.py, GPIOR0's import swapped for an equivalent
        // scratch RAM ptr -- the same adaptation tests/oracle/sweep.py makes.
        var firmware = PymcuCompiler.BuildSourceRp2040(
            "from pymcu.types import ptr, uint8\n" +
            "GPIOR0: ptr[uint8] = ptr(0x20041FF0)\n" +
            "s = GPIOR0.value\n" +
            "cells: list[list[uint8]] = [[s, s + 1], [s + 2, s + 3]]\n" +
            "y: uint8 = 1\n" +
            "row = cells[y]\n" +
            "row[0] = 9\n" +
            "print(row[0])\n" +
            "print(cells[1][0])\n" +
            "print(cells[0][0])\n" +
            "\n" +
            "\n" +
            "def bump_first_row(grid: list[list[uint8]], v: uint8):\n" +
            "    r = grid[0]\n" +
            "    r[1] = v\n" +
            "    return r[0]\n" +
            "\n" +
            "\n" +
            "print(bump_first_row(cells, 42))\n" +
            "print(cells[0][1])\n" +
            "\n" +
            "total = 0\n" +
            "for yy in range(2):\n" +
            "    r2 = cells[yy]\n" +
            "    r2[1] = yy + 7\n" +
            "    total = total + r2[1]\n" +
            "print(total)\n" +
            "print(\"END\")\n" +
            "while True:\n" +
            "    pass\n");

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue();

        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        // Matches the oracle's own CPython reference run for this probe exactly
        // (s == 0): 9, 9, 0, 0, 42, then total == 7 + 8 == 15.
        sb.ToString().Should().Be("9\n9\n0\n0\n42\n15\nEND\n");
    }
}
