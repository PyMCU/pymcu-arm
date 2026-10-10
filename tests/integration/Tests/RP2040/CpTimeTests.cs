using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// `time.monotonic()` and `supervisor.ticks_ms()` on RP2040 (PyMCU#cp-timebase-arm).
///
/// Before the fix, the CircuitPython compat layer imported millis()/millis_init()
/// straight from pymcu.hal.timer, which has no RP2040/RP2350 branch and raises
/// "Timer not supported on this architecture" at its own module top level --
/// every program that named monotonic(), monotonic_ns(), ticks_ms() or a
/// TimeAlarm refused to compile at all, even though pymcu.time (the module they
/// should have gone through) already reads the RP2040 hardware TIMER directly.
///
/// WHAT DISCRIMINATES: both readers must advance together across three real
/// 50 ms sleeps -- a timebase frozen at the HAL's unreachable branch would never
/// have compiled in the first place, and a wrong register would not keep the
/// two in lockstep the way the hardware TIMER backing both of them does.
/// </summary>
[TestFixture]
public class CpTimeTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("cp-time-rp2040");

    [Test]
    public void MonotonicAndTicksMsAdvanceTogether()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);

        pico.RunUntilOutput(pico.Uart0, "DONE", timeoutMs: 20_000).Should().BeTrue(
            "the program must compile and run to completion, not refuse at build time");

        pico.Uart0.Text.Should().Contain(
            "RDY\n0.0\n0\n0.05\n50\n0.1\n100\nDONE\n",
            because: "time.monotonic() (seconds) and supervisor.ticks_ms() (milliseconds) " +
                     "read the same hardware TIMER and must agree after each 50 ms sleep");
    }
}
