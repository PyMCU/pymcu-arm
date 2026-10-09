using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// RFC 0009 port to ARM, oracle probe 230_optional_early_return_narrowing.py: a tagged
/// Optional forwarded through a SECOND function boundary (bump() re-returns grab()'s own
/// tag/payload after narrowing with `is None`) -- exercises that a tag read out of one
/// Call.TagDst is itself a plain uint8 Return.Tag at the next boundary, not something that
/// needs re-deriving.
/// </summary>
[TestFixture]
public class OptionalEarlyReturnNarrowingTests
{
    // Oracle probe 230_optional_early_return_narrowing.py, GPIOR0's import swapped for an
    // equivalent scratch RAM ptr -- the same adaptation tests/oracle/sweep.py makes.
    private const string Source =
        "try:\n" +
        "    from typing import Optional\n" +
        "except ImportError:\n" +
        "    pass\n" +
        "from pymcu.types import ptr, uint8\n" +
        "GPIOR0: ptr[uint8] = ptr(0x20041FF0)\n" +
        "from pymcu.hal.console import print\n" +
        "\n" +
        "\n" +
        "def grab(k: uint8) -> Optional[uint8]:\n" +
        "    if k == 0:\n" +
        "        return None\n" +
        "    return k\n" +
        "\n" +
        "\n" +
        "def bump(k: uint8) -> Optional[uint8]:\n" +
        "    v = grab(k)\n" +
        "    if v is None:\n" +
        "        return None\n" +
        "    return v + 1\n" +
        "\n" +
        "\n" +
        "r1 = bump(GPIOR0.value + 4)\n" +
        "r2 = bump(GPIOR0.value)\n" +
        "print(r1 or 0)\n" +
        "print(r2 or 0)\n" +
        "if r1 is not None:\n" +
        "    print(r1)\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    public void OptionalForwardedThroughNestedCall_MatchesCPython()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        // GPIOR0's scratch byte boots to 0: r1 = bump(4) = grab(4)+1 = 5, r2 = bump(0) =
        // None (grab(0) is None) -- matches the oracle's own CPython reference run.
        Text(pico).Should().Be(
            "5\n0\n5\nEND\n",
            "a tag read from one Call.TagDst must survive as an ordinary Return.Tag "
            + "at the next function boundary");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
