using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Oracle probe 511: a `float[N]` array element store/load funnels through the same
/// NarrowFromI32/WidenToI32 helpers every other narrower-than-i32 slot uses
/// (ArrayStore/ArrayLoad, FieldStore/FieldLoad, BufferStore/BufferLoad all reach them), which
/// picked the instruction purely from the LLVM type name not being "i32": `trunc` going in,
/// `sext`/`zext` coming out. Both are integer narrowing/widening ops -- invalid IR when the
/// element is FLOAT, since a loaded `float` register is a different kind, not a narrower
/// integer. `opt` rejected it outright: "invalid cast opcode for cast from 'i32' to 'float'".
/// Fixed by bitcasting (reinterpreting the 32 bits) instead of trunc/sext/zext when the slot
/// type is FLOAT -- the same exception CompileBitcast's own comment already carries for the
/// explicit IR Bitcast instruction, just never extended to these two implicit-move helpers.
/// </summary>
[TestFixture]
public class FloatArrayElementTests
{
    // Oracle probe 511_float_array_elements.py verbatim, GPIOR0's import swapped for an
    // equivalent scratch RAM ptr -- the same adaptation tests/oracle/sweep.py makes for
    // this probe. Exercises both a plain float[N] local array (ArrayStore/ArrayLoad) and a
    // float[N] instance field (FieldStore/FieldLoad) -- both funnel through the same
    // NarrowFromI32/WidenToI32 helpers the fix changed.
    private const string Source =
        "from pymcu.types import ptr, uint8, inline\n" +
        "GPIOR0: ptr[uint8] = ptr(0x20041FF0)\n" +
        "\n" +
        "s = GPIOR0.value\n" +
        "\n" +
        "f: float[3] = [0.0] * 3\n" +
        "f[s] = s + 1234.5\n" +
        "f[s + 2] = s - 0.25\n" +
        "f[s + 2] += s + 1.0\n" +
        "print(f[s], f[1], f[s + 2], f[-1])\n" +
        "\n" +
        "\n" +
        "class Acc:\n" +
        "    def __init__(self):\n" +
        "        self.v: float[70] = [0.0] * 70\n" +
        "\n" +
        "    def put(self, i: uint8, x: float):\n" +
        "        self.v[i] = x\n" +
        "\n" +
        "    def get(self, i: uint8) -> float:\n" +
        "        return self.v[i]\n" +
        "\n" +
        "\n" +
        "a = Acc()\n" +
        "a.put(s + 69, s + 2.5)\n" +
        "a.v[s + 68] = s - 1.25\n" +
        "print(a.get(s + 69), a.v[s + 68], a.v[s])\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    public void FloatArrayElement_StoreAndLoad_RoundTripTheRealBits()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        // GPIOR0's scratch byte boots to 0, so this matches CPython's own run of the probe
        // (the oracle's reference run shows "1234.5 0.0 0.75 0.75\n2.5 -1.25 0.0\nEND\n").
        // Before the fix this never got this far: opt failed the build outright.
        Text(pico).Should().Be(
            "1234.5 0.0 0.75 0.75\n2.5 -1.25 0.0\nEND\n",
            "a float array element must round-trip through bitcast, not trunc/sext/zext");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
