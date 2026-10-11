using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// The RP2040 float arithmetic is the bootrom's mufplib, which has no NaN handling:
/// `inf - inf` answers 0.0 where CPython answers nan, silently. Every value below is
/// derived from a byte read over UART at run time (b = 0), so nothing folds to a
/// compile-time constant (the C# folder computes NaN correctly and would make the test
/// vacuous). The shims in crt0.S wrap the ROM and return the canonical NaN for the
/// IEEE invalid operations, propagate a NaN operand, and make every ordered compare
/// with a NaN false.
///
/// Expected output is CPython's, line for line. `0.0 / 0.0` is not in the list: CPython raises
/// ZeroDivisionError there, so there is nothing to match.
/// </summary>
[TestFixture]
public class FloatRomNanTests
{
    private const string Source = """
        from pymcu.types import uint8
        from pymcu.hal.uart import UART


        def main():
            uart = UART(9600)
            uart.println("GO")
            b: uint8 = uart.read_blocking()
            z: float = float(b)
            inf: float = 1e300 * (z + 1e300)
            ninf: float = -inf
            nan: float = float('nan') + z
            one: float = z + 1.0
            two: float = z + 2.0

            print(inf - inf != inf - inf)
            print(inf + ninf != inf + ninf)
            print(z * inf != z * inf)
            print(inf * z != inf * z)
            print(inf / inf != inf / inf)
            print(nan + one != nan + one)
            print(one - nan != one - nan)
            print(nan * two != nan * two)
            print(nan / two != nan / two)
            print(nan == nan)
            print(nan != nan)
            print(nan < one)
            print(nan > one)
            print(nan <= one)
            print(nan >= one)
            print(one < nan)
            print(one > nan)
            print(one <= nan)
            print(one >= nan)
            print(one == nan)
            print(one != nan)
            print(inf - one == inf)
            print(inf + inf == inf)
            print(one - one == z)
            print(one < two)
            print(inf > two)
            print(ninf < one)
            print(inf == inf)
            print(inf - inf)
            print(nan)
            print(inf)
            print(ninf)
            print(one + one)

            while True:
                pass
        """;

    private const string Expected =
        "True\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n" +          // 9 NaN producers/propagators
        "False\nTrue\nFalse\nFalse\nFalse\nFalse\nFalse\nFalse\nFalse\nFalse\nFalse\nTrue\n" + // compares with nan
        "True\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n" +                      // fast path stays exact
        "nan\nnan\ninf\n-inf\n2.0\n";

    [Test]
    public void NanAndInfArithmetic_MatchesCPython_OnRp2040()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "GO", timeoutMs: 20_000)
            .Should().BeTrue("the firmware must reach its banner");
        pico.Uart0.InjectByte(0);

        pico.RunUntilOutput(
            pico.Uart0,
            _ => Text(pico).Count(c => c == '\n') >= 34,
            timeoutMs: 60_000);

        Text(pico).Replace("GO\n", "").Replace("\r", "").Should().Be(Expected);
    }

    // ---- every pair of {0, -0, 1, -2.5, 3e38, inf, -inf, nan} through + - * / and the six compares ----

    private static readonly (string Name, string Init, float Value)[] Operands =
    {
        ("zero", "z", 0f),
        ("nzero", "-z", -0f),
        ("one", "z + 1.0", 1f),
        ("neg", "z - 2.5", -2.5f),
        ("big", "z + 3e38", 3e38f),
        ("inf", "1e300 * (z + 1e300)", float.PositiveInfinity),
        ("ninf", "-(1e300 * (z + 1e300))", float.NegativeInfinity),
        ("nan", "float('nan') + z", float.NaN),
    };

    private static readonly (string Sym, Func<float, float, float> F)[] Ops =
    {
        ("+", (a, b) => a + b),
        ("-", (a, b) => a - b),
        ("*", (a, b) => a * b),
        ("/", (a, b) => a / b),
    };

    private static string MatrixSource()
    {
        var sb = new StringBuilder();
        sb.AppendLine("from pymcu.types import uint8, uint32, bitcast");
        sb.AppendLine("from pymcu.hal.uart import UART");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def dump(r: float):");
        sb.AppendLine("    bits: uint32 = bitcast(uint32, r)");
        sb.AppendLine("    print(bits)");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def cmpd(a: float, b: float):");
        sb.AppendLine("    print(a == b, a != b, a < b, a > b, a <= b, a >= b)");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("def main():");
        sb.AppendLine("    uart = UART(9600)");
        sb.AppendLine("    uart.println(\"GO\")");
        sb.AppendLine("    b: uint8 = uart.read_blocking()");
        sb.AppendLine("    z: float = float(b)");
        foreach (var (n, init, _) in Operands) sb.AppendLine($"    {n}: float = {init}");
        foreach (var (sym, _) in Ops)
            foreach (var (an, _, _) in Operands)
                foreach (var (bn, _, bv) in Operands)
                    if (!(sym == "/" && bv == 0f))
                        sb.AppendLine($"    dump({an} {sym} {bn})");
        foreach (var (an, _, _) in Operands)
            foreach (var (bn, _, _) in Operands)
                sb.AppendLine($"    cmpd({an}, {bn})");
        sb.AppendLine("    while True:");
        sb.AppendLine("        pass");
        return sb.ToString();
    }

    private static string Canon(uint bits)
        => (bits & 0x7FFFFFFFu) > 0x7F800000u ? "nan" : bits.ToString();

    [Test]
    public void EveryPairOfSpecialValues_MatchesIeee_OnRp2040()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(MatrixSource());

        var expected = new List<string>();
        foreach (var (sym, f) in Ops)
            foreach (var (_, _, a) in Operands)
                foreach (var (_, _, b) in Operands)
                    if (!(sym == "/" && b == 0f))
                        expected.Add(Canon(BitConverter.SingleToUInt32Bits(f(a, b))));
        foreach (var (_, _, a) in Operands)
            foreach (var (_, _, b) in Operands)
                expected.Add($"{a == b} {a != b} {a < b} {a > b} {a <= b} {a >= b}");

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "GO", timeoutMs: 20_000)
            .Should().BeTrue("the firmware must reach its banner");
        pico.Uart0.InjectByte(0);
        pico.RunUntilOutput(
            pico.Uart0,
            _ => Text(pico).Count(c => c == '\n') >= expected.Count + 1,
            timeoutMs: 300_000);

        var got = Text(pico).Replace("\r", "").Split('\n').Skip(1).Take(expected.Count).ToList();
        for (int i = 0; i < got.Count; i++)
            if (i < ArithCount() && uint.TryParse(got[i], out var gb)) got[i] = Canon(gb);

        var lines = new List<string>();
        int k = 0;
        foreach (var (sym, _) in Ops)
            foreach (var (an, _, _) in Operands)
                foreach (var (bn, _, bv) in Operands)
                {
                    if (sym == "/" && bv == 0f) continue;   // CPython raises ZeroDivisionError; the compiler matches
                    if (k < got.Count && got[k] != expected[k]) lines.Add($"{an} {sym} {bn}: expected {expected[k]} got {got[k]}");
                    k++;
                }
        foreach (var (an, _, _) in Operands)
            foreach (var (bn, _, _) in Operands)
            {
                if (k < got.Count && got[k] != expected[k]) lines.Add($"cmp({an}, {bn}) ==,!=,<,>,<=,>=: expected {expected[k]} got {got[k]}");
                k++;
            }
        got.Count.Should().Be(expected.Count);
        lines.Should().BeEmpty(because: "every line must match IEEE-754 float32");
    }

    private static int ArithCount() => 3 * Operands.Length * Operands.Length + Operands.Length * (Operands.Length - 2);

    private static string Text(PicoSimulation pico)
    {
        var sb = new StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
