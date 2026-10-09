using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// `except ValueError as e: print(e.args[0])` printed garbage bytes instead of the
/// raise's string message. The shared IRGenerator (ControlFlow.cs/Call.cs/OptionalUnion.cs)
/// hardcoded the exception-message pointer's storage ("__exn_msg" and its per-handler
/// snapshots) to DataType.UINT16 -- correct for AVR's 16-bit address space, but on a
/// 32-bit target the flash string's real address no longer fits: the upper 16 bits were
/// dropped on the way into the slot, and uart_write_str then read from the wrong,
/// unrelated location. Fixed by using DataTypeExtensions.PointerType() (already the
/// established pattern for "ptr" and "bytearray" annotations), which resolves to UINT32
/// on a 32-bit target and stays UINT16 on AVR -- same value as before there, so AVR's
/// generated code is unchanged.
/// </summary>
[TestFixture]
public class ExceptionMessagePointerWidthTests
{
    private const string Source =
        "def risky():\n" +
        "    raise ValueError(\"bad\")\n" +
        "try:\n" +
        "    risky()\n" +
        "except ValueError as e:\n" +
        "    print(e.args[0])\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    public void ExceptionArgsMessage_PrintsTheRealTextNotGarbage()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        Text(pico).Should().Contain("bad\nEND",
            "the message pointer must survive a 32-bit address, not just AVR's 16-bit one");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
