using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// `not x` on a RUN-TIME float: adafruit_bmp280's `.altitude` property reads
/// `.pressure`, whose division-by-zero guard is `if not var1:` where var1 is a
/// computed float, not a literal. The shared IR hands the backend a Unary Not
/// over a float source (a COMPILE-TIME constant float already folds in
/// VisitUnary), and Rp2040LlvmCodeGen.CompileUnary used to refuse it outright
/// ("ARM backend: float unary op Not"). It now emits the same ordered `fcmp oeq`
/// against 0.0 that BinaryOp.Equal uses, which reads -0.0 as zero and NaN as
/// non-zero, matching CPython. AVR needs no equivalent: its CompileFloatUnary
/// masks the sign bit and ORs the four bytes.
///
/// Every value below is derived from a byte read over UART at run time
/// (b = 0), so nothing folds to a compile-time FloatConstant. The NaN comes
/// from float('nan') + zero, not from inf - inf: the RP2040 bootrom float
/// library (mufplib) has no NaN handling and answers inf - inf with 0.0, so
/// that spelling tests the ROM, not `not`. CPython: not 0.0 -> True,
/// not -0.0 -> True, not 1.5 -> False, not nan -> False, not inf -> False.
/// </summary>
[TestFixture]
public class FloatUnaryNotTests
{
    private const string Source = """
        from pymcu.types import uint8
        from pymcu.hal.uart import UART


        def main():
            uart = UART(9600)
            uart.println("GO")
            b: uint8 = uart.read_blocking()
            zero: float = float(b)
            negzero: float = -zero
            onehalf: float = zero + 1.5
            big: float = 1e300 * (zero + 1e300)
            nan: float = float('nan') + zero

            print(not zero)
            print(not negzero)
            print(not onehalf)
            print(not nan)
            print(not big)

            while True:
                pass
        """;

    [Test]
    public void NotOfARuntimeFloat_MatchesCPython_OnRp2040()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "GO", timeoutMs: 20_000)
            .Should().BeTrue("the firmware must reach its banner");
        pico.Uart0.InjectByte(0);

        pico.RunUntilOutput(
            pico.Uart0,
            _ => Text(pico).Count(c => c == '\n') >= 6,
            timeoutMs: 20_000);

        Text(pico).Should().Contain(
            "True\nTrue\nFalse\nFalse\nFalse\n",
            because: "not 0.0, not -0.0, not 1.5, not nan, not inf must read " +
                     "True/True/False/False/False, exactly as CPython does");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
