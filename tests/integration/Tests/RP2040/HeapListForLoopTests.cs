using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// GC heap port to ARM, oracle probe 153: `for v in xs:` over a heap list -- the
/// frontend's own generic iteration lowering (index 0..len, EmitElemAddr per step), no
/// GC-specific support needed beyond the Return/Call-style boundary the other tests
/// exercise directly.
/// </summary>
[TestFixture]
public class HeapListForLoopTests
{
    [Test]
    public void ForLoopOverAHeapList_SumsLikeCPython()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(
            "from pymcu.types import uint8\n" +
            "xs: list[uint8] = list()\n" +
            "xs.append(2)\n" +
            "xs.append(3)\n" +
            "xs.append(4)\n" +
            "total = 0\n" +
            "for v in xs:\n" +
            "    total = total + v\n" +
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
        sb.ToString().Should().Be("9\nEND\n");
    }
}
