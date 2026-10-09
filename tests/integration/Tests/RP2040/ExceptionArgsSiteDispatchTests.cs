using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Oracle probe 550 (pymcu-avr tests/oracle/probes/550_oserror_int_argument_args_semantics.py,
/// GPIOR0 import swapped for an equivalent scratch RAM ptr the way the ARM oracle sweep
/// adapts it): `print(e.args)` / `print(e.args[0])` of an integer raise argument replays the
/// raise site's own recorded piece through a per-program dispatch function
/// (__pymcu_print_exn_args) that `switch`es on a site id written at each raise.
///
/// NOT a PyMCU/LLVM bug -- ignored, not fixed. With four distinct raise sites, opt -O2 turns
/// the IRGenerator's if/else chain over site ids into an LLVM `switch i8`, which llc -O2 lowers
/// to a jump table on thumbv6m (Cortex-M0/M0+ has no TBB/TBH) using the Thumb-1 idiom
/// "ADD Rd, PC; LDRB; LSL; ADD PC, Rd". That GENERATED CODE is architecturally correct Thumb-1
/// (verified against the ARM ARM's PC-read rule: a hi-register op with PC as an operand reads
/// the address of THAT instruction + 4). The emulator this test suite runs on, RP2040Sharp,
/// does not evaluate it correctly -- isolated with a two-line standalone repro, no PyMCU
/// runtime at all: a function containing only `asm("add %0, pc", r); return r` returns neither
/// its own instruction's address+4 (the architecturally correct value) NOR anything sensibly
/// related to it; across three independent programs the returned value instead matched some
/// OTHER unrelated "ADD Rd, PC" site elsewhere in the same binary, offset by +2 -- consistent
/// with the interpreter resolving this operand form from a stale/precomputed value tied to
/// execution order rather than recomputing PC per instruction. Filed against RP2040Sharp, not
/// fixed here (separate repo). Raising llc's jump-table threshold for thumbv6m (an earlier,
/// reverted commit) made this pass by avoiding the instruction form entirely, but that would
/// ship different (larger) code to REAL silicon to work around a test-only emulator gap --
/// exactly the "verde sin demandante" this project's own doctrine warns against. Kept as a RED,
/// explicitly-ignored test rather than silently skipped so this does not quietly regress once
/// RP2040Sharp's ADD-Rd,PC handling is fixed.
/// </summary>
[TestFixture]
public class ExceptionArgsSiteDispatchTests
{
    // pymcu.chips.atmega328p.GPIOR0 is AVR-only; stand in with an equivalent scratch RAM
    // byte near the top of RP2040's SRAM, the same adaptation tests/oracle/sweep.py makes
    // for this probe. Real, zero-initialized, read/write -- not a folded constant, so the
    // first raise's argument is a genuine non-constant OSError arg, like the real probe.
    private const string Source =
        "from pymcu.types import ptr, uint8\n" +
        "GPIOR0: ptr[uint8] = ptr(0x20041FF0)\n" +
        "\n" +
        "try:\n" +
        "    raise OSError(GPIOR0.value + 110)\n" +
        "except OSError as e:\n" +
        "    print(e.args)\n" +
        "    print(e.args[0])\n" +
        "    print(len(e.args))\n" +
        "\n" +
        "def deeper():\n" +
        "    raise OSError(5)\n" +
        "\n" +
        "try:\n" +
        "    deeper()\n" +
        "except OSError as e:\n" +
        "    print(e.args[0], len(e.args))\n" +
        "\n" +
        "try:\n" +
        "    raise TimeoutError(11)\n" +
        "except OSError as e:\n" +
        "    print(e.args[0], len(e.args))\n" +
        "\n" +
        "try:\n" +
        "    raise ValueError(7)\n" +
        "except ValueError as e:\n" +
        "    print(e.args[0])\n" +
        "\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    [Ignore("RP2040Sharp mis-executes the Thumb-1 \"ADD Rd, PC\" jump-table idiom llc -O2 "
            + "emits for a 4-site dispatch (see class doc) -- not a PyMCU/LLVM bug. Red on "
            + "purpose until the emulator is fixed, so a real regression here is not masked.")]
    public void FourRaiseSites_EachPrintsItsOwnArgument()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue($"--- uart ---\n{Text(pico)}");

        // Matches CPython exactly (GPIOR0 boots to 0, so the first raise's argument is
        // 0 + 110). Before the fix, the first two lines came back "(,)" and "" (the
        // non-constant OSError site's piece never printed) and the TimeoutError handler
        // printed ValueError's "7" instead of "11" -- the jump table landed on the wrong
        // case, not merely a missing one.
        Text(pico).Should().Be(
            "(110,)\n110\n1\n5 1\n11 1\n7\nEND\n",
            "every raise site must replay its OWN recorded argument, not an empty tuple "
            + "or a neighboring site's");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
