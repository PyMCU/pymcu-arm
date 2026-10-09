using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;
using RP2350.TestKit.Probes;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// RP2350 half of the RP2040 UnhandledRaiseAutoInitTests fix (#038): a program that never
/// calls print() or constructs a UART never auto-inits UART0, so __pymcu_unhandled_exn used
/// to find TXEN clear and halt without printing anything. Rp2040LlvmCodeGen.EmitExnRuntime's
/// "uart_init" block is shared between rp2040 and rp2350 (only the register bases and the
/// reset bit numbers differ, both read from ResolveTarget()/the per-chip constants), so this
/// pins the same contract on the M33 path.
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
        var firmware = PymcuCompiler.BuildSourceRp2350(Source);

        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(firmware)
            .AddUartProbe(0, out UartProbe uart);

        sim.WaitForUart(uart, "E:ValueError", timeoutMs: 20_000).ConditionMet
            .Should().BeTrue("an uncaught raise must halt by printing E:<Name>, same as AVR, "
                              + "even when the program itself never touched the console");
    }
}
