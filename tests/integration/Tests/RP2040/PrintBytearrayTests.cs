using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// `print(buf)` on a bytearray silently answered "0" instead of CPython's
/// `bytearray(b'...')` repr. TryEmitByteArrayReprArg (src/compiler/IR/IRGenerator/Call.cs)
/// streams one call to a shared `uart_write_byte_repr` helper per byte, resolved by
/// name through the program's own console/UART HAL module -- the AVR console
/// (lib/src/pymcu/hal/avr/uart/avr.py) defines it, the RP family's
/// (lib/src/pymcu/hal/rp/console.py) never did. Finding none, the resolver returned
/// null, the repr path gave up, and the array fell through to the scalar print path
/// that answers 0 for an array value -- never a CompileError, just a wrong number.
/// </summary>
[TestFixture]
public class PrintBytearrayTests
{
    private const string Source =
        "buf = bytearray(b\"\\xcc\\x10\\xca\\xfe\")\n" +
        "print(buf)\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    public void PrintOfABytearray_MatchesCPythonsRepr()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        Text(pico).Should().Contain("bytearray(b'\\xcc\\x10\\xca\\xfe')",
            "the repr must show the buffer's real bytes, not the scalar fallback's 0");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
