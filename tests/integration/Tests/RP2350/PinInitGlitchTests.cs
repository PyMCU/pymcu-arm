using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// machine.Pin.init(mode=Pin.OUT, value=1) must put the value in the output latch
/// BEFORE enabling the driver: writing OE first lets a zero latch drive the pad low
/// until the value write lands -- a real low pulse on the wire.
/// The packaged RP2040 sim has no GPIO-change hook, so this runs on the RP2350 one,
/// whose SioPeripheral.OnGpioChanged fires on every OUT/OE register write.
/// </summary>
[TestFixture]
public class PinInitGlitchTests
{
    private const int Pin = 4;

    private const string Source = """
        from machine import Pin
        def main():
            p = Pin(4, Pin.IN)
            p.init(Pin.OUT, value=1)
            while True:
                pass
        """;

    [Test]
    public void Init_OutValue1_NeverDrivesLow()
    {
        var firmware = PymcuCompiler.BuildSourceRp2350(Source, "micropython");
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(firmware);

        var sio = sim.Machine.Sio;
        bool observedDrivenLow = false;
        sio.OnGpioChanged += () =>
        {
            // A change that leaves the driver enabled on a low latch is the glitch.
            if (sio.GetGpioOutputEnable(Pin) && !sio.GetGpioOut(Pin))
                observedDrivenLow = true;
        };

        sim.RunMilliseconds(10);

        observedDrivenLow.Should().BeFalse(
            "init(OUT, value=1) must write the output latch before OE -- " +
            "a low pulse here means the driver was enabled first");
        sio.GetGpioOutputEnable(Pin).Should().BeTrue("the pin ends configured as output");
        sio.GetGpioOut(Pin).Should().BeTrue("the pin ends driving high");
    }

    [Test]
    public void Init_PullNone_LeavesPadPullsOff()
    {
        // pull=None is upstream's "disable pulls" spelling. The PADS reset state has
        // PDE set, so the write must actually happen -- both bits read back clear.
        // (The ctor's pull is positional: @inline overloads dispatch on positional
        // argument types, so pull= cannot name it there.)
        var firmware = PymcuCompiler.BuildSourceRp2350("""
            from machine import Pin
            def main():
                p = Pin(6, Pin.IN, None)
                q = Pin(7, Pin.IN)
                q.init(pull=None)
                while True:
                    pass
            """, "micropython");
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm)
            .WithBinary(firmware);
        sim.RunMilliseconds(10);

        var pads = sim.Machine.Sio.PadsBank0!;
        foreach (var pad in new[] { 6u, 7u })
        {
            var reg = pads.ReadWord(4 + 4 * pad);
            (reg & (1u << 2)).Should().Be(0, $"GP{pad}: pull=None must clear PDE");
            (reg & (1u << 3)).Should().Be(0, $"GP{pad}: pull=None must clear PUE");
        }
    }
}
