using System.Globalization;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;
using RP2350.Peripherals;
using RP2350.TestKit;

// Mirrors the AVR oracle runner's contract (pymcu-avr/tests/oracle/runner/Program.cs): run a compiled
// firmware image until "END\n" shows up on the console UART, or a timeout elapses, and print whatever
// the UART captured (partial output included) so the Python harness can diff it against CPython.
//
// Usage: PyMCU.OracleRunner.ARM <chip: rp2040|rp2350> <firmware.bin path> <max_ms>
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: PyMCU.OracleRunner.ARM <rp2040|rp2350> <firmware.bin> <max_ms>");
    return 2;
}

var chip = args[0];
var firmwarePath = args[1];
var maxMs = double.Parse(args[2], CultureInfo.InvariantCulture);
var firmware = File.ReadAllBytes(firmwarePath);

switch (chip)
{
    case "rp2040":
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        try
        {
            pico.RunUntilOutput(pico.Uart0, "END\n", timeoutMs: maxMs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[runner] rp2040 run raised: {ex}");
        }
        Console.Write(pico.Uart0.Text.Replace("\r\n", "\n"));
        return 0;
    }
    case "rp2350":
    {
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm).WithBinary(firmware);
        sim.AddUartProbe(0, out var uart);
        try
        {
            sim.WaitForUart(uart, "END\n", timeoutMs: maxMs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[runner] rp2350 run raised: {ex}");
        }
        Console.Write(uart.Text.Replace("\r\n", "\n"));
        Console.Error.WriteLine(
            $"[runner] rp2350 hardfaults={sim.HardFaultCount} lockup={sim.IsLockedUp}");
        return 0;
    }
    default:
        Console.Error.WriteLine($"[runner] unknown chip '{chip}'");
        return 2;
}
