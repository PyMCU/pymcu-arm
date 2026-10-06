using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// `not x` on a value only known at run time. The backend numbered the zext before the
/// icmp it reads, so every such `not` produced IR that `opt` rejects ("instruction
/// expected to be numbered ..."), and the build failed. The CircuitPython blink
/// `led.value = not led.value` is the common way to reach it. A constant `not` folds in
/// the frontend and never got here, which is why the operand comes from the UART.
/// </summary>
[TestFixture]
public class NotOperatorTests
{
    private const string Source =
        "from pymcu.types import uint8\n" +
        "from pymcu.hal.uart import UART\n\n\n" +
        "def main():\n" +
        "    uart = UART(9600)\n" +
        "    uart.println(\"GO\")\n" +
        "    s: uint8 = uart.read_blocking()\n" +
        "    z: bool = not s\n" +
        "    print(uint8(z))\n" +
        "    s = s + 1\n" +
        "    o: bool = not s\n" +
        "    print(uint8(o))\n" +
        "    while True:\n" +
        "        pass\n";

    [Test]
    public void NotOfARuntimeValue_BuildsAndMatchesPython()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "GO", timeoutMs: 20_000)
            .Should().BeTrue("the firmware must reach its banner");
        pico.Uart0.InjectByte(0);
        pico.RunUntilOutput(pico.Uart0, _ => Text(pico).Count(c => c == '\n') >= 3, timeoutMs: 20_000);

        // not 0 -> True, not 1 -> False.
        Lines(pico).Skip(1).Take(2).Should().Equal(new[] { "1", "0" },
            $"--- uart ---\n{Text(pico)}");
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
