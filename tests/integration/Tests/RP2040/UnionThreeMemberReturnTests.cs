using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// RFC 0009 port to ARM, oracle probe 270_union_three_member_return.py: a declared
/// Union[int, float, None] return. The payload slot is always FLOAT-widened (the widest
/// member, same AVR convention), so this is the test that caught the real bug in the
/// port: TaggedPayloadI32 must judge whether to bitcast by the VALUE's own type, not by
/// the function's widest/declared ReturnType -- a `return k` arm handing back the int
/// member is genuinely int-typed, and a LoadF32-then-bitcast on it (mis-keyed on
/// ReturnType == FLOAT) silently read zero instead of the int bits.
/// </summary>
[TestFixture]
public class UnionThreeMemberReturnTests
{
    // Oracle probe 270_union_three_member_return.py, GPIOR0's import swapped for an
    // equivalent scratch RAM ptr -- the same adaptation tests/oracle/sweep.py makes.
    private const string Source =
        "from typing import Union\n" +
        "from pymcu.types import ptr, uint8\n" +
        "GPIOR0: ptr[uint8] = ptr(0x20041FF0)\n" +
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
        "a = pick(GPIOR0.value)\n" +
        "b = pick(GPIOR0.value + 1)\n" +
        "c = pick(GPIOR0.value + 7)\n" +
        "\n" +
        "if isinstance(a, int):\n" +
        "    print(a)\n" +
        "elif isinstance(a, float):\n" +
        "    print(a)\n" +
        "else:\n" +
        "    print(\"a-none\")\n" +
        "\n" +
        "if isinstance(b, int):\n" +
        "    print(b)\n" +
        "elif isinstance(b, float):\n" +
        "    print(b)\n" +
        "else:\n" +
        "    print(\"b-none\")\n" +
        "\n" +
        "if isinstance(c, int):\n" +
        "    print(c)\n" +
        "elif isinstance(c, float):\n" +
        "    print(c)\n" +
        "else:\n" +
        "    print(\"c-none\")\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    public void UnionIntFloatNoneReturn_NarrowsToTheLiveMember_MatchesCPython()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        // GPIOR0's scratch byte boots to 0: a = pick(0) = None, b = pick(1) = 2.5,
        // c = pick(7) = 7 (the int member) -- matches the oracle's own CPython run.
        Text(pico).Should().Be(
            "a-none\n2.5\n7\nEND\n",
            "the int member's payload must round-trip as its own i32 bits, not as a "
            + "float-bitcast of the widest member's type");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
