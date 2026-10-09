using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// RFC 0009 port to ARM, oracle probe 276_union_field_forwarding.py: a union living in
/// an object field (the field's own storage is the widest-member type, same convention
/// as a tagged Call.Dst) forwarded through a getter.
///
/// `b.v = 2.5` -- a bare float LITERAL stored directly into the field with no
/// intervening arithmetic -- caught a second, independent pre-existing gap: LoadI32 had
/// no case for a raw FloatConstant operand (only WidenToI32 bitcast a FLOAT-typed
/// Temporary/Variable; a literal never lands in one first unless some operation forces
/// it). CompileArrayStore/CompileStoreIndirect call LoadI32 unconditionally regardless of
/// element type, so this threw "operand 'FloatConstant' cannot be read yet" outright.
/// Fixed by giving LoadI32 a FloatConstant case that reads its bits via an LLVM
/// constant-expression bitcast (`bitcast (float ... to i32)`), computed at compile time.
/// </summary>
[TestFixture]
public class UnionFieldForwardingTests
{
    // Oracle probe 276_union_field_forwarding.py, GPIOR0's import swapped for an
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
        "class Box:\n" +
        "    def __init__(self) -> None:\n" +
        "        self.v: Union[int, float, None] = None\n" +
        "        self.n = 0\n" +
        "\n" +
        "    def get(self) -> Union[int, float, None]:\n" +
        "        return self.v\n" +
        "\n" +
        "\n" +
        "b = Box()\n" +
        "b.v = GPIOR0.value + 5\n" +
        "r = b.get()\n" +
        "if isinstance(r, int):\n" +
        "    print(r)\n" +
        "elif isinstance(r, float):\n" +
        "    print(r)\n" +
        "else:\n" +
        "    print(\"none\")\n" +
        "\n" +
        "b.v = 2.5\n" +
        "r2 = b.get()\n" +
        "if isinstance(r2, float):\n" +
        "    print(r2)\n" +
        "elif isinstance(r2, int):\n" +
        "    print(r2)\n" +
        "else:\n" +
        "    print(\"none\")\n" +
        "\n" +
        "b.v = pick(GPIOR0.value)\n" +
        "r3 = b.get()\n" +
        "if r3 is None:\n" +
        "    print(\"r3-none\")\n" +
        "elif isinstance(r3, int):\n" +
        "    print(r3)\n" +
        "elif isinstance(r3, float):\n" +
        "    print(r3)\n" +
        "else:\n" +
        "    print(\"r3-?\")\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    public void UnionField_ForwardedThroughGetter_MatchesCPython()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        // GPIOR0's scratch byte boots to 0: b.v = 5 (int) -> r = 5; b.v = 2.5 (float
        // literal) -> r2 = 2.5; b.v = pick(0) = None -> r3 = None -- matches the
        // oracle's own CPython reference run.
        Text(pico).Should().Be(
            "5\n2.5\nr3-none\nEND\n",
            "a union field's tag must survive a direct literal store and a getter call "
            + "the same way a plain tagged return does");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
