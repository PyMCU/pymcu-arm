using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Oracle probe 038 (pymcu-avr tests/oracle/probes/038_unhandled_raise.py): a program that
/// never calls print() or constructs a UART, and whose only raise is never caught, used to
/// hang instead of halting with "E:&lt;Name&gt;" like AVR does.
///
/// Root cause: UART0 is only initialized (reset deasserted, baud programmed, pins muxed to
/// the UART function) as a side effect of the program itself using print()/UART() --
/// Rp2040LlvmCodeGen never emits that setup for a program that does not. __pymcu_unhandled_exn
/// checked UART0's enable bit before printing and, finding it clear (nothing ever turned it
/// on), branched straight past the report to the halt loop -- correct for a program that
/// deliberately turned ITS OWN UART off, wrong for one that never had a UART at all. AVR's
/// EmitExnRuntime (AvrCodeGen.cs, issue #340) already draws this distinction via
/// DeviceConfig.UartOwnedByProgram: owned-and-off means respect it and halt silently,
/// not-owned-and-off means the runtime itself may turn UART0 on (at [tool.pymcu] stdout_baud)
/// so the report can print. Rp2040LlvmCodeGen.EmitExnRuntime now does the same -- see its
/// "uart_init" block.
/// </summary>
[TestFixture]
public class UnhandledRaiseAutoInitTests
{
    private const string Source =
        "def fail():\n" +
        "    raise ValueError(\"boom\")\n" +
        "fail()\n";

    [Test]
    public void NoConsoleEverUsed_UncaughtRaiseStillHaltsPrintingExceptionName()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        // Before the fix this timed out: __pymcu_unhandled_exn found UART0 off (nothing in
        // the program had ever turned it on) and branched straight to the halt loop without
        // printing anything.
        pico.RunUntilOutput(pico.Uart0, "E:ValueError", timeoutMs: 20_000)
            .Should().BeTrue("an uncaught raise must halt by printing E:<Name>, same as AVR, "
                              + "even when the program itself never touched the console");
    }
}
