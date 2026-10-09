using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// The GC heap (list[T]/array.array) ported to ARM: mark-and-compact with a shadow
/// stack (Rp2040LlvmCodeGen.EmitGcRuntime), gated on arch="arm" in the shared compiler
/// (PyMCU, GcAnalysisPhase/Assign.cs). Oracle probes 084 (append/len/index) and 225/226
/// (module-level read/write) -- the simplest shape, no function boundary, no growth
/// past the first allocation's capacity.
///
/// This was the probe that caught the real bug in the port: list[T]'s entire pointer-
/// offset arithmetic reinterpreted the GC_REF heap address as UINT16 before adding an
/// offset (correct for AVR's 16-bit address space, but RP2040/RP2350 RAM starts at
/// 0x20000000) -- the very first `xs.append(2)` truncated the address to garbage and
/// raised MemoryError instead of growing the list. Fixed in the shared compiler
/// (Assign.cs/Call.cs/Expr.cs's GcPtrWidth()), not here.
/// </summary>
[TestFixture]
public class HeapListAppendAndIndexTests
{
    private static string Run(string body)
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(body);
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue();
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }

    [Test]
    public void AppendThenLenAndIndex_MatchesCPython()
    {
        // Oracle probe 084_heap_list.py, unmodified (no chip-specific seed).
        Run(
            "from pymcu.types import uint8\n" +
            "xs: list[uint8] = list()\n" +
            "xs.append(2)\n" +
            "xs.append(3)\n" +
            "print(len(xs))\n" +
            "print(xs[0] + xs[1])\n" +
            "print(\"END\")\n" +
            "while True:\n" +
            "    pass\n")
            .Should().Be("2\n5\nEND\n");
    }

    [Test]
    public void ModuleLevelReadsAfterThreeAppends_MatchCPython()
    {
        // Oracle probes 225/226 (identical bodies, two names for the same shape).
        Run(
            "from pymcu.types import uint8\n\n" +
            "xs: list[uint8] = list()\n" +
            "xs.append(4)\n" +
            "xs.append(5)\n" +
            "xs.append(6)\n" +
            "print(xs[0])\n" +
            "print(xs[1])\n" +
            "print(xs[2])\n" +
            "print(\"END\")\n" +
            "while True:\n" +
            "    pass\n")
            .Should().Be("4\n5\n6\nEND\n");
    }
}
