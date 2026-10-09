using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// GC heap port to ARM, oracle probes 207 (array.array), 208 (list(), sum through a
/// function) and 227 (an element read through a function): list[T] has no fixed-width
/// ABI, so a plain function's list parameter is expanded at each call site -- the same
/// mechanism a class-instance parameter already uses -- rather than passed as a normal
/// value. Nothing GC-specific here either: the expansion is frontend work, inherited for
/// free once the heap itself (CompileGcAlloc/GcRoot/GcUnroot) works.
/// </summary>
[TestFixture]
public class HeapListFunctionParamTests
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
    public void ArrayArrayPassedToAFunction_SumsLikeCPython()
    {
        // Oracle probe 207_array_array_param.py, unmodified.
        Run(
            "import array\n" +
            "from pymcu.types import uint8\n\n" +
            "def total(xs: list[uint8], n: uint8) -> uint8:\n" +
            "    s = 0\n" +
            "    for i in range(n):\n" +
            "        s = s + xs[i]\n" +
            "    return s\n\n" +
            "a = array.array(\"B\", [4, 5, 6])\n" +
            "print(total(a, 3))\n" +
            "print(\"END\")\n" +
            "while True:\n" +
            "    pass\n")
            .Should().Be("15\nEND\n");
    }

    [Test]
    public void HeapListPassedToAFunction_SumsLikeCPython()
    {
        // Oracle probe 208_list_param_sum.py, unmodified.
        Run(
            "from pymcu.types import uint8\n\n" +
            "def total(xs: list[uint8], n: uint8) -> uint8:\n" +
            "    s = 0\n" +
            "    for i in range(n):\n" +
            "        s = s + xs[i]\n" +
            "    return s\n\n" +
            "xs: list[uint8] = list()\n" +
            "xs.append(4)\n" +
            "xs.append(5)\n" +
            "xs.append(6)\n" +
            "print(total(xs, 3))\n" +
            "print(\"END\")\n" +
            "while True:\n" +
            "    pass\n")
            .Should().Be("15\nEND\n");
    }

    [Test]
    public void ElementReadThroughAFunction_MatchesCPython()
    {
        // Oracle probe 227_list_param_elem_read.py, unmodified.
        Run(
            "from pymcu.types import uint8\n\n" +
            "def head2(ys: list[uint8]) -> uint8:\n" +
            "    return ys[1]\n\n" +
            "xs: list[uint8] = list()\n" +
            "xs.append(4)\n" +
            "xs.append(5)\n" +
            "xs.append(6)\n" +
            "print(head2(xs))\n" +
            "print(\"END\")\n" +
            "while True:\n" +
            "    pass\n")
            .Should().Be("5\nEND\n");
    }
}
