using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// pwmio.PWMOut on the RP2040. The layer only imported the PWM HAL on AVR, so every
/// PWMOut (and with it adafruit_motor's Servo) died with "call to undefined function
/// '_PWM'". The HAL also lacked frequency(), set_freq() and deinit(), which the layer
/// calls, and could not make a 50 Hz servo signal at all: TOP is 16 bits, so at the full
/// 125 MHz clock the slowest period was ~1.9 kHz. The slice divider now takes the slack.
///
/// A 50 Hz channel on GP7 (the Feather's D5, slice 3, channel B) at 50 %, as the PWM HAL
/// divides the clock (the divider in sixteenths, the smallest that brings the period under 65536):
///   period  = 125000000 // 50                 = 2500000
///   divider = (2500000 >> 12) + 1 = 611 / 16  = 38.19 (the register's integer part reads 38)
///   TOP     = 2500000 * 16 // 611 - 1          = 65465
///   compare = 65466 * 32768 >> 16              = 32733
/// </summary>
[TestFixture]
public class CpPwmioTests
{
    private const string Feather = "adafruit_feather_rp2040";

    private const string Source = """
        import board
        import pwmio
        from pymcu.types import ptr, uint32


        def main():
            pwm = pwmio.PWMOut(board.D5, duty_cycle=32768, frequency=50)
            base = 0x40050000 + 3 * 0x14
            div: ptr[uint32] = ptr(base + 0x04)
            top: ptr[uint32] = ptr(base + 0x10)
            cc: ptr[uint32] = ptr(base + 0x0C)
            ctrl: ptr[uint32] = ptr(0x40014000 + 8 * 7 + 4)
            print(div.value >> 4)
            print(top.value)
            print(cc.value >> 16)
            print(ctrl.value & 31)
            print(pwm.frequency)
            pwm.duty_cycle = 16384
            print(cc.value >> 16)
            pwm.deinit()
            print(ctrl.value & 31)
            while True:
                pass
        """;

    [Test]
    public void AServoSignal_GetsADividerAndTheRightCompare()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040(Source, "circuitpython", Feather);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, _ => Text(pico).Count(c => c == '\n') >= 7, timeoutMs: 20_000);

        Text(pico).Should().StartWith("38\n65465\n32733\n4\n50\n16366\n5\n",
            because: "divider 38.19, TOP 65465, 50 % compare, pin on PWM (4), 50 Hz reported, " +
                     "25 % compare after the setter, pin back on SIO (5) after deinit");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }
}
