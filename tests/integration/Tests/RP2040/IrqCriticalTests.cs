using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// pymcu.hal.irq on ARM: enable_interrupts() / disable_interrupts() lower to the
/// Cortex-M PRIMASK instructions instead of folding away to nothing (which left
/// every critical section on RP2040/RP2350 silently unprotected). The snippets
/// are emitted as full compiler barriers so LLVM cannot move the guarded body
/// out of the CPSID I / CPSIE I pair.
/// </summary>
[TestFixture]
public class IrqCriticalTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("irq-critical-rp2040");

    [Test]
    public void CriticalSectionRuns_AndInterruptsComeBackOn()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        pico.RunUntilOutput(pico.Uart0, "OK", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("C:3");   // three guarded increments of a global
    }

    [Test]
    public void PrimaskInstructionsSurviveOptimization()
    {
        var ll = File.ReadAllText(Path.Combine(
            PymcuCompiler.ExampleDir("irq-critical-rp2040"), "dist", "debug", "firmware.opt.ll"));

        // Three inlined disable/enable pairs from bump(), plus main's explicit
        // enable_interrupts() (the outlined copy of bump() adds one more pair).
        CountOf(ll, "\"cpsid i\"").Should().BeGreaterThanOrEqualTo(3);
        CountOf(ll, "\"cpsie i\"").Should().BeGreaterThanOrEqualTo(4);
        ll.Should().Contain("asm sideeffect \"cpsid i\", \"~{memory}\"");
        ll.Should().Contain("asm sideeffect \"cpsie i\", \"~{memory}\"");

        // And they reach machine code: Thumb CPSID I = 0xB672, CPSIE I = 0xB662.
        CountBytes(_firmware, 0x72, 0xB6).Should().BeGreaterThanOrEqualTo(3);
        CountBytes(_firmware, 0x62, 0xB6).Should().BeGreaterThanOrEqualTo(4);
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static int CountBytes(byte[] image, byte lo, byte hi)
    {
        int n = 0;
        for (int i = 0; i + 1 < image.Length; i++)
            if (image[i] == lo && image[i + 1] == hi) n++;
        return n;
    }
}
