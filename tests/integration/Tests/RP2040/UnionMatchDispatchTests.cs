using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// RFC 0009 port to ARM, oracle probe 272_union_match_dispatch.py: `match` on a tagged
/// union -- `case None:`/`case int():`/`case float():` compare the tag byte the Call.
/// TagDst boundary already unpacked, and a literal arm (`case 7:`) compares the payload
/// after that same unpacking. No match-specific backend support exists or is needed: the
/// tag is an ordinary uint8 by the time match's own generic lowering ever sees it.
/// </summary>
[TestFixture]
public class UnionMatchDispatchTests
{
    // Oracle probe 272_union_match_dispatch.py, GPIOR0's import swapped for an
    // equivalent scratch RAM ptr -- the same adaptation tests/oracle/sweep.py makes.
    private const string Source =
        "from typing import Union\n" +
        "from pymcu.types import ptr, uint8\n" +
        // GPIOR0 is WATCHDOG_SCRATCH0, not a byte of SRAM. See Rp2040Scratch for why
        // the probe needs a byte that reads 0 at start and sits outside the stack.
        "GPIOR0: ptr[uint8] = ptr(" + Rp2040Scratch.GPIOR0Address + ")\n" +
        "from pymcu.hal.console import print\n" +
        "\n" +
        "\n" +
        "def pick(k: int) -> Union[int, float, None]:\n" +
        "    if k == 0:\n" +
        "        return None\n" +
        "    if k == 1:\n" +
        "        return 2.5\n" +
        "    return k\n" +
        "\n" +
        "\n" +
        "r = pick(GPIOR0.value + 1)\n" +
        "match r:\n" +
        "    case None:\n" +
        "        print(\"none\")\n" +
        "    case int():\n" +
        "        print(\"int\", r)\n" +
        "    case float():\n" +
        "        print(\"float\", r)\n" +
        "    case _:\n" +
        "        print(\"wild\")\n" +
        "\n" +
        "c = pick(GPIOR0.value + 7)\n" +
        "match c:\n" +
        "    case 7:\n" +
        "        print(\"seven\")\n" +
        "    case int():\n" +
        "        print(\"int\", c)\n" +
        "    case _:\n" +
        "        print(\"wild\")\n" +
        "\n" +
        "n = pick(GPIOR0.value)\n" +
        "match n:\n" +
        "    case None:\n" +
        "        print(\"is-none\")\n" +
        "    case _:\n" +
        "        print(\"wild\")\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    public void MatchOnTaggedUnion_DispatchesByTagAndPayload_MatchesCPython()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        // GPIOR0's scratch byte boots to 0: r = pick(1) = 2.5 (float arm), c = pick(7) = 7
        // (the literal arm, not the int() arm), n = pick(0) = None -- matches the
        // oracle's own CPython reference run.
        Text(pico).Should().Be(
            "float 2.5\nseven\nis-none\nEND\n",
            "match's tag/payload comparisons must see the same unpacked values an "
            + "isinstance check would");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
