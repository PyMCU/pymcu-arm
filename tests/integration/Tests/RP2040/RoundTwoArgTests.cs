using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Oracle probes 536/556/557: `round(x, n)` on a float used to fail the opt build outright
/// with "use of undefined value '...digs0'". pymcu.round2's half-to-even path declares a
/// SECOND scratch array (`digs0: uint8[1] = [0]`, separate from the 16-byte `digs` the first
/// path uses) that it only ever passes BY ADDRESS to `_float_fmt_digits` -- never indexed
/// again in round2 itself. Its one-element zero initializer folds away as the redundant
/// store it is (the backing global already reads 0), which is correct DCE -- but it was the
/// only ArrayStore/ArrayLoad instruction telling the backend this array needs space at all,
/// so `digs0`'s global declaration never got emitted and `ptrtoint ptr @..._digs0` referenced
/// nothing.
///
/// Fixed upstream in PyMCU-armrepr: IRGenerator now serializes ArrayByteSizes (every array's
/// byte size, module-level or function-local -- GlobalArrays only ever carried the former,
/// deliberately, so AVR's stack overlay keeps aliasing locals across sibling calls exactly as
/// before). Rp2040LlvmCodeGen now also scans ArrayBase operands (an array passed by address,
/// e.g. as a call argument) and falls back to ArrayByteSizes for exactly this shape: an array
/// whose own ArrayStore/ArrayLoad instructions all folded away.
/// </summary>
[TestFixture]
public class RoundTwoArgTests
{
    // Oracle probe 536_round_two_arg_float.py, GPIOR0's import swapped for an equivalent
    // scratch RAM ptr -- the same adaptation tests/oracle/sweep.py makes for this probe.
    private const string Source =
        "from pymcu.types import ptr, uint8\n" +
        "GPIOR0: ptr[uint8] = ptr(0x20041FF0)\n" +
        "\n" +
        "s = GPIOR0.value\n" +
        "x: float = float(s) / 7.0 + 3.14159\n" +
        "\n" +
        "print(round(x, 2))\n" +
        "print(round(3.14159, 2))\n" +
        "print(round(2.5, 0))\n" +
        "print(round(-2.675, 2))\n" +
        "print(round(1.005, 2))\n" +
        "print(round(100.0, -1))\n" +
        "print(round(0.125, 2))\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    public void RoundWithPrecision_MatchesCPythonHalfToEven()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        // GPIOR0's scratch byte boots to 0 (x == 3.14159 + 0/7.0), so this matches the
        // oracle's own CPython reference run exactly. Before the fix the build never got
        // this far: opt rejected the IR outright.
        Text(pico).Should().Be(
            "3.14\n3.14\n2.0\n-2.67\n1.0\n100.0\n0.12\nEND\n",
            "round(x, n)'s second scratch array must get a global the same as the first");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
