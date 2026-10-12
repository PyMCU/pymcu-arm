using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// The size of a heap object's payload is kept in 16 bits of its header, so 65535 bytes is the
/// most one object can hold on this chip (AVR's header has a single length byte, 255). A request
/// above it used to be stored truncated; it is now refused like any allocation that does not fit,
/// which the program sees as MemoryError. Below the ceiling, objects far past AVR's 255 bytes
/// allocate and read back (oracle probe 840 is measured on AVR only for that reason).
/// </summary>
[TestFixture]
public class HeapObjectCeilingTests
{
    private static string Run(string body)
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(body);
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "END", timeoutMs: 20_000)
            .Should().BeTrue();
        var sb = new System.Text.StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }

    private static string Probe(int count) =>
        "from pymcu.types import uint8\n\n" +
        "try:\n" +
        $"    x: list[uint8] = list({count})\n" +
        "    x.append(7)\n" +
        "    print(len(x), x[0])\n" +
        "except MemoryError:\n" +
        "    print(\"MemoryError\")\n" +
        "y: list[uint8] = list(10)\n" +
        "y.append(5)\n" +
        "print(y[0])\n" +
        "print(\"END\")\n" +
        "while True:\n" +
        "    pass\n";

    [Test]
    public void AnObjectPastTheAvrCeilingIsAllocated() =>
        Run(Probe(1000)).Should().Be("1 7\n5\nEND\n");

    [Test]
    public void AnObjectNearTheHeaderLimitIsAllocated() =>
        Run(Probe(65000)).Should().Be("1 7\n5\nEND\n");

    [Test]
    public void AnObjectAboveTheHeaderLimitRaisesMemoryErrorAndTheHeapStaysUsable() =>
        Run(Probe(70000)).Should().Be("MemoryError\n5\nEND\n");
}
