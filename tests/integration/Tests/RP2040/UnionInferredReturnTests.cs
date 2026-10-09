using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// RFC 0009 port to ARM, oracle probe 271_union_inferred_return.py: an UNANNOTATED
/// function whose member list is inferred from its own return statements (int on one
/// arm, float on another, None at the end) -- the frontend decides ReturnMembers the
/// same way a declared Union does, so the backend sees the identical Return.Tag/
/// Call.TagDst shape as UnionThreeMemberReturnTests with no annotation to read.
/// </summary>
[TestFixture]
public class UnionInferredReturnTests
{
    // Oracle probe 271_union_inferred_return.py, GPIOR0's import swapped for an
    // equivalent scratch RAM ptr -- the same adaptation tests/oracle/sweep.py makes.
    private const string Source =
        "from pymcu.types import ptr, uint8\n" +
        "GPIOR0: ptr[uint8] = ptr(0x20041FF0)\n" +
        "from pymcu.hal.console import print\n" +
        "\n" +
        "\n" +
        "def guess(k: int):\n" +
        "    if k == 0:\n" +
        "        return 5\n" +
        "    if k == 1:\n" +
        "        return 2.5\n" +
        "    return None\n" +
        "\n" +
        "\n" +
        "a = guess(GPIOR0.value)\n" +
        "b = guess(GPIOR0.value + 1)\n" +
        "c = guess(GPIOR0.value + 3)\n" +
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
    public void UnannotatedInferredUnionReturn_MatchesCPython()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        // GPIOR0's scratch byte boots to 0: a = guess(0) = 5, b = guess(1) = 2.5,
        // c = guess(3) = None -- matches the oracle's own CPython reference run.
        Text(pico).Should().Be(
            "5\n2.5\nc-none\nEND\n",
            "an inferred (unannotated) union return must carry the same tag the "
            + "declared-Union shape carries");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
