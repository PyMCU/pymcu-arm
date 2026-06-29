using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class RtosCoopTests
{
    private static byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("rtos-coop-blink");

    [Test]
    public void TwoTasks_RunCooperatively_BothToggle()
    {
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(_firmware);

        // Task A toggles GP24, task B toggles GP25; they hand off via taskYIELD().
        // If the context switch works, BOTH pins must toggle (not just the first).
        var sio = pico.Rp2040.Sio;
        bool prevA = sio.GetGpioOut(24), prevB = sio.GetGpioOut(25);
        int togglesA = 0, togglesB = 0;
        for (int i = 0; i < 200; i++)
        {
            pico.RunInstructions(2000);
            bool a = sio.GetGpioOut(24), b = sio.GetGpioOut(25);
            if (a != prevA) { togglesA++; prevA = a; }
            if (b != prevB) { togglesB++; prevB = b; }
        }

        togglesA.Should().BeGreaterThan(2, "task A must run and toggle GP24");
        togglesB.Should().BeGreaterThan(2, "task B must run and toggle GP25 — proving the context switch hands off");
    }
}
