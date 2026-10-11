using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// busio.SPI(board.SCK, MOSI=board.MOSI) leaves MISO out, which CircuitPython reads as
/// "no MISO pin" (a write-only bus: the 74HC595 shift register passes only MOSI). The layer
/// handed the HAL the default None, and the HAL's `miso: const` refused it at build time
/// ("Parameter 'miso' declared const ... 'MISO' varies at runtime"). The layer now passes
/// -1, which the HAL reads as "do not route this pin".
///
/// The program reads back GPIO16's (the Pico's MISO) and GPIO19's (MOSI) FUNCSEL: the bus
/// must claim MOSI for SPI (1) and leave MISO at its reset value (31, null).
/// </summary>
[TestFixture]
public class CpSpiUnroutedPinTests
{
    private const string Source = """
        import board
        import busio
        from pymcu.types import ptr, uint32


        def main():
            spi = busio.SPI(board.SCK, MOSI=board.MOSI)
            miso_ctrl: ptr[uint32] = ptr(0x40014000 + 8 * 16 + 4)
            mosi_ctrl: ptr[uint32] = ptr(0x40014000 + 8 * 19 + 4)
            print(mosi_ctrl.value & 31)
            print(miso_ctrl.value & 31)
            while True:
                pass
        """;

    [Test]
    public void ASpiBuiltWithoutMiso_BuildsAndLeavesThatPinUnclaimed()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source, "circuitpython", "raspberry_pi_pico");

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, _ => Text(pico).Count(c => c == '\n') >= 2, timeoutMs: 20_000);

        Text(pico).Should().StartWith("1\n31\n",
            because: "MOSI is routed to SPI0 (FUNCSEL 1) and the MISO pin is never touched");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
