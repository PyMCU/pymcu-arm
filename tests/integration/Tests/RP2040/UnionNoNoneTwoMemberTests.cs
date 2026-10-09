using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// RFC 0009 port to ARM, oracle probe 273_union_no_none_two_member.py: a Union without
/// None (int, float) still takes a tag -- which member a call returns is only a
/// run-time fact, same as a None-carrying union, just with the None member missing from
/// Function.ReturnMembers.
/// </summary>
[TestFixture]
public class UnionNoNoneTwoMemberTests
{
    // Oracle probe 273_union_no_none_two_member.py, GPIOR0's import swapped for an
    // equivalent scratch RAM ptr -- the same adaptation tests/oracle/sweep.py makes.
    private const string Source =
        "from typing import Union\n" +
        "from pymcu.types import ptr, uint8\n" +
        "GPIOR0: ptr[uint8] = ptr(0x20041FF0)\n" +
        "from pymcu.hal.console import print\n" +
        "\n" +
        "\n" +
        "def pick2(k: int) -> Union[int, float]:\n" +
        "    if k == 0:\n" +
        "        return 9\n" +
        "    return 1.5\n" +
        "\n" +
        "\n" +
        "a = pick2(GPIOR0.value)\n" +
        "b = pick2(GPIOR0.value + 1)\n" +
        "\n" +
        "if isinstance(a, float):\n" +
        "    print(a)\n" +
        "else:\n" +
        "    print(a)\n" +
        "\n" +
        "if isinstance(b, float):\n" +
        "    print(b)\n" +
        "else:\n" +
        "    print(b)\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    public void UnionWithoutNone_TwoMembers_MatchesCPython()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        // GPIOR0's scratch byte boots to 0: a = pick2(0) = 9 (int), b = pick2(1) = 1.5
        // (float) -- matches the oracle's own CPython reference run.
        Text(pico).Should().Be(
            "9\n1.5\nEND\n",
            "a Union without None still needs a member tag when the live member is "
            + "only a run-time fact");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
