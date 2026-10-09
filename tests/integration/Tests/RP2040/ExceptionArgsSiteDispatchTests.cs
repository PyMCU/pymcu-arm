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
/// With four distinct raise sites in one program (as this probe has: a non-constant OSError,
/// a constant OSError via a called function, a constant TimeoutError, and a constant
/// ValueError), opt -O2 turns the IRGenerator's if/else chain over site ids into an LLVM
/// `switch i8`, which `llc -O2` then lowers to a jump table on thumbv6m (Cortex-M0/M0+,
/// rp2040's target) -- this target has no TBB/TBH (ARMv7-M+ only), so llc hand-rolls the
/// table with "ADD Rd, PC; LDRB; LSL; ADD PC, Rd", and at -O2 that sequence is miscompiled:
/// the dispatch lands on the wrong site's piece (or on none). Confirmed by bisecting opt/llc
/// optimization levels independently -- only "both opt -O2 AND llc -O2" reproduces it -- and
/// by raising llc's jump-table threshold, which makes it fall back to a plain compare chain
/// and fixes the output with no other change. Fixed in Rp2040LlvmToolchain.assemble (and
/// assemble_natmod) by passing llc "-min-jump-table-entries=1000000" for thumbv6m: Cortex-M0+
/// never gets a jump table from llc, so it never hits the broken lowering. RP2350 (cortex-m33,
/// thumbv8m) is untouched -- it has real TBB/TBH and was not observed to reproduce this.
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
