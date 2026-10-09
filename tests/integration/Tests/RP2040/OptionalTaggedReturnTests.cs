using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// RFC 0009 port to ARM, oracle probe 229_optional_tagged_return.py: the simplest
/// Optional[uint8] return shape -- a function whose ReturnMembers is non-empty returns
/// payload + a member-index tag byte packed as the uniform LLVM `{ i32, i8 }`
/// (Rp2040LlvmCodeGen.CompileReturn/CompileCall), unpacked at the call site before
/// `is None`/`or` read the ordinary byte each lowers to. No union-specific logic is
/// needed beyond that Return/Call boundary -- isinstance/is-None/`or` are already
/// generic IR on an ordinary uint8 variable.
/// </summary>
[TestFixture]
public class OptionalTaggedReturnTests
{
    // Oracle probe 229_optional_tagged_return.py, GPIOR0's import swapped for an
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
        "def read(k: uint8) -> Optional[uint8]:\n" +
        "    if k == 0:\n" +
        "        return None\n" +
        "    return k + 7\n" +
        "\n" +
        "\n" +
        "a = read(GPIOR0.value)\n" +
        "b = read(GPIOR0.value + 5)\n" +
        "if a is None:\n" +
        "    print(\"a-none\")\n" +
        "else:\n" +
        "    print(a)\n" +
        "if b is not None:\n" +
        "    print(b)\n" +
        "else:\n" +
        "    print(\"b-none\")\n" +
        "print(a or 1)\n" +
        "print(b or 1)\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    public void OptionalReturn_PayloadAndTagRoundTrip_MatchesCPython()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        // GPIOR0's scratch byte boots to 0: a = read(0) = None, b = read(5) = 12 --
        // matches the oracle's own CPython reference run exactly.
        Text(pico).Should().Be(
            "a-none\n12\n1\n12\nEND\n",
            "a tagged Optional return must carry None through the Call/Return boundary");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
