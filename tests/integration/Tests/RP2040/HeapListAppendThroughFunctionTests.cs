using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// GC heap port to ARM, oracle probe 228: a function that appends and GROWS the list
/// past its initial capacity (triggering gc_alloc for the larger buffer, the
/// gc_list_fixup call that repoints every alias -- not just the appended-through
/// parameter -- at the new buffer, and the byte-copy of the old buffer's contents) does
/// it all on behalf of the module-level `xs` the caller still reads afterward. Exercises
/// the full realloc path end to end, through the parameter-expansion boundary.
/// </summary>
[TestFixture]
public class HeapListAppendThroughFunctionTests
{
    [Test]
    public void AppendThroughAFunction_GrowsTheCallersOwnList()
    {
        // Oracle probe 228_list_param_append.py, unmodified.
        var firmware = PymcuCompiler.BuildSourceRp2040(
            "from pymcu.types import uint8\n\n" +
            "def grow(ys: list[uint8], v: uint8) -> None:\n" +
            "    ys.append(v)\n\n" +
            "xs: list[uint8] = list()\n" +
            "xs.append(4)\n" +
            "grow(xs, 9)\n" +
            "grow(xs, 7)\n" +
            "print(xs[0])\n" +
            "print(xs[1])\n" +
            "print(xs[2])\n" +
            "print(\"END\")\n" +
            "while True:\n" +
            "    pass\n");

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue();

        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        sb.ToString().Should().Be("4\n9\n7\nEND\n");
    }
}
