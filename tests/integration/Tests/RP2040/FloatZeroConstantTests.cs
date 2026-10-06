using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// The backend wrote a bare `0` where LLVM needed a float zero, and `opt` rejected the
/// module ("integer constant must have integer type"). Two places reached it:
///
///   * the error return of a float function. The stdlib's float formatting divides, so
///     the division-by-zero guard gives it one, and any `print()` of a float failed;
///   * the initializer of a module-level float global.
///
/// Both programs read their operand from the UART so nothing folds away.
/// </summary>
[TestFixture]
public class FloatZeroConstantTests
{
    private const string PrintFloat =
        "from pymcu.types import uint8\n" +
        "from pymcu.hal.uart import UART\n\n\n" +
        "def main():\n" +
        "    uart = UART(9600)\n" +
        "    uart.println(\"GO\")\n" +
        "    s: uint8 = uart.read_blocking()\n" +
        "    f: float = float(s) / 4.0\n" +
        "    print(f)\n" +
        "    while True:\n" +
        "        pass\n";

    private const string ModuleFloat =
        "from pymcu.types import uint8\n" +
        "from pymcu.hal.uart import UART\n\n" +
        "scale: float = 0.0\n\n\n" +
        "def main():\n" +
        "    global scale\n" +
        "    uart = UART(9600)\n" +
        "    uart.println(\"GO\")\n" +
        "    s: uint8 = uart.read_blocking()\n" +
        "    scale = float(s) * 2.5\n" +
        "    print(uint8(scale))\n" +
        "    while True:\n" +
        "        pass\n";

    [Test]
    public void PrintOfAFloat_Builds()
        => RunWith(PrintFloat, input: 10).Should().Be("2.5");

    [Test]
    public void ModuleLevelFloatGlobal_Builds()
        => RunWith(ModuleFloat, input: 2).Should().Be("5");

    private static string RunWith(string source, byte input)
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "GO", timeoutMs: 20_000)
            .Should().BeTrue("the firmware must reach its banner");
        pico.Uart0.InjectByte(input);
        pico.RunUntilOutput(pico.Uart0, _ => Text(pico).Count(c => c == '\n') >= 2, timeoutMs: 20_000);

        return Lines(pico).ElementAtOrDefault(1) ?? $"<no output>\n{Text(pico)}";
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }

    private static string[] Lines(PicoSimulation pico)
        => Text(pico).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
}
