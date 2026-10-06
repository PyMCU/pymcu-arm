using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// The RP2350 muxes UARTs differently from the RP2040: IS_VALID_TX/RX there takes
/// every even/odd pad, not (pin &amp; 3). GP2/GP3 are a UART0 pair on the RP2350 and
/// an impossible pair on the RP2040, which is what proves the per-chip table.
/// </summary>
[TestFixture]
public class Rp2350UartPadTests
{
    [Test]
    public void MpUart_Gp2Gp3_IsAUart0PairOnRp2350()
        => PymcuCompiler.BuildSourceRp2350("""
            from machine import Pin, UART
            u = UART(0, 115200, tx=Pin(2), rx=Pin(3), timeout=100)
            """, "micropython").Should().NotBeEmpty(
            "GP2/GP3 are UART0 pads on the RP2350 (the RP2040 table refuses them)");

    [Test]
    public void MpUart_Uart1Pads_AreRefusedOnRp2350()
    {
        // GP4/GP5 are UART1's pair on the RP2350 too; the HAL still drives UART0.
        Action build = () => PymcuCompiler.BuildSourceRp2350("""
            from machine import Pin, UART
            u = UART(0, 115200, tx=Pin(4), rx=Pin(5))
            """, "micropython");
        build.Should().Throw<InvalidOperationException>().WithMessage("*UART0*");
    }

    [Test]
    public void CpUart_Gp2Gp3_IsAUart0PairOnRp2350()
        // The CP board module only ships raspberry_pi_pico, so name the pads
        // literally -- busio pins are plain integers anyway.
        => PymcuCompiler.BuildSourceRp2350("""
            import busio
            uart = busio.UART(2, 3, baudrate=115200, timeout=0.1, receiver_buffer_size=32)
            """, "circuitpython").Should().NotBeEmpty(
            "GP2/GP3 are a UART0 pair on the RP2350, so busio.UART must accept them");
}
