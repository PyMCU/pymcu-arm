namespace PyMCU.IntegrationTests;

public sealed class SignedDivModProgram
{
    public string Source { get; }
    public byte InputByte => 0;
    public IReadOnlyList<long> Expected { get; }

    private SignedDivModProgram(string source, List<long> expected) => (Source, Expected) = (source, expected);

    private static (long q, long r) FloorDivMod(long a, long b, int bytes)
    {
        long q = a / b;
        long r = a - q * b;
        if (r != 0 && (r < 0) != (b < 0)) { q -= 1; r += b; }
        return (Wrap(q, bytes), Wrap(r, bytes));
    }

    private static long Wrap(long v, int bytes) => bytes switch
    {
        1 => (sbyte)v,
        2 => (short)v,
        _ => (int)v,
    };

    private static string Lit(long v) => v == int.MinValue ? "(-2147483647 - 1)" : v.ToString();

    private static IEnumerable<(long a, long b)> Pairs(long min, long max) => new (long, long)[]
    {
        (7, 3), (7, -3), (-7, 3), (-7, -3),
        (8, 2), (8, -2), (-8, 2), (-8, -2),
        (7, 7), (-7, 7), (7, -7), (-7, -7),
        (2, 7), (-2, 7), (2, -7), (-2, -7),
        (0, 3), (0, -3),
        (100, 1), (-100, 1), (100, -1), (-100, -1),
        (max, 3), (min, 3), (max, -3), (min, -3),
        (max, -1), (min, -1),
        (min, min), (max, max), (min, max), (max, min),
    };

    public static SignedDivModProgram Generate(string typeName, int bytes)
    {
        long min = bytes switch { 1 => sbyte.MinValue, 2 => short.MinValue, _ => int.MinValue };
        long max = bytes switch { 1 => sbyte.MaxValue, 2 => short.MaxValue, _ => int.MaxValue };

        var pairs = Pairs(min, max).ToList();
        var expected = new List<long>();
        var src = new System.Text.StringBuilder();

        src.Append($"from pymcu.types import {typeName}, uint8\n");
        src.Append("from pymcu.hal.uart import UART\n\n\n");
        src.Append("def main():\n");
        src.Append("    uart = UART(9600)\n");
        src.Append("    uart.println(\"GO\")\n");
        src.Append("    s: uint8 = uart.read_blocking()\n");
        src.Append($"    base: {typeName} = {typeName}(s)\n");

        int n = 0;
        foreach (var (a, b) in pairs)
        {
            src.Append($"    a{n}: {typeName} = base + {Lit(a)}\n");
            src.Append($"    b{n}: {typeName} = base + {Lit(b)}\n");
            src.Append($"    q{n}: {typeName} = a{n} // b{n}\n");
            src.Append($"    r{n}: {typeName} = a{n} % b{n}\n");

            var (q, r) = FloorDivMod(a, b, bytes);
            for (int k = 0; k < bytes; k++)
            {
                src.Append($"    print(uint8(q{n} >> {8 * k}))\n");
                expected.Add((q >> (8 * k)) & 0xFF);
            }
            for (int k = 0; k < bytes; k++)
            {
                src.Append($"    print(uint8(r{n} >> {8 * k}))\n");
                expected.Add((r >> (8 * k)) & 0xFF);
            }
            n++;
        }

        src.Append("    while True:\n        pass\n");
        return new SignedDivModProgram(src.ToString(), expected);
    }
}
