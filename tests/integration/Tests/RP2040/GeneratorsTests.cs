using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Generators on RP2040 (same fixture contract as the AVR GeneratorsTests).
/// </summary>
[TestFixture]
public class GeneratorsTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("generators-rp2040");

    [Test]
    public void YieldsSumAndBreak()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        pico.RunUntilOutput(pico.Uart0, "F:8", timeoutMs: 20_000).Should().BeTrue();
        pico.Uart0.Should().Contain("GEN\n1\n2\n4\n8\nS:15");
    }
}
