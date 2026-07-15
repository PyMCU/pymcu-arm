using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// async/await v2: awaits inside if/elif/else, `while cond` and for-range
/// (CFG state splitting), continue in a flattened loop, `return expr` exposing
/// the result via `_value`, and the asyncio.gather executor driving two
/// coroutines of different classes concurrently.
/// </summary>
[TestFixture]
public class AsyncV2Tests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("async-v2-rp2040");

    [Test]
    public void ControlFlowAwaits_GatherAndReturnValue()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);
        pico.RunUntilOutput(pico.Uart0, "T:13", timeoutMs: 20_000)
            .Should().BeTrue("worker(4) sums 1+1+10+1 across if/for awaits");
        pico.Uart0.Should().Contain("P");
    }
}
