using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Operand-form inline asm on ARM (%N placeholders lowered to LLVM tied
/// read-write constraints) and SIO hardware division correctness with the
/// PRIMASK critical section in place.
/// </summary>
[TestFixture]
public class AsmOpsTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("asm-ops-rp2040");

    [Test]
    public void AsmOperands_AndIrqSafeDivision()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        pico.RunUntilOutput(pico.Uart0, "6", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("42");   // asm("adds %0, %0, #1", a) on 41
        pico.Uart0.Should().Contain("52");   // asm("adds %0, %0, %1", b=10, c=42)
        pico.Uart0.Should().Contain("13");   // 40 // 3 through the guarded divider
    }
}
