using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// The RP2350 muxes UARTs differently from the RP2040: IS_VALID_TX/RX there takes
/// every even/odd pad, not (pin &amp; 3). But a pad numbered 2 mod 4 (TX) or 3 mod 4
/// (RX) reaches its UART only through GPIO_FUNC_UART_AUX, and the RP2350 UART HAL
/// writes plain GPIO_FUNC_UART on every pad -- so a UART accepted on GP2/GP3 would
/// mux the pins to a function that is not the UART and drive nothing. Those pads
/// are refused; the per-chip table is still proven because GP12/GP13 are a UART0
/// pair on the RP2350 and an impossible pair on the RP2040.
/// </summary>
[TestFixture]
public class Rp2350UartPadTests
{
    [Test]
    public void MpUart_Gp2Gp3_NeedUartAux_AreRefused()
        => AssertAuxRefused("micropython", """
            from machine import Pin, UART
            u = UART(0, 115200, tx=Pin(2), rx=Pin(3), timeout=100)
            """);

    [Test]
    public void MpUart_Gp2Gp3_InInit_NeedUartAux_AreRefused()
        => AssertAuxRefused("micropython", """
            from machine import Pin, UART
            u = UART(0, 115200, tx=Pin(12), rx=Pin(13))
            u.init(9600, 8, None, 1, tx=Pin(2), rx=Pin(3))
            """);

    [Test]
    public void CpUart_Gp2Gp3_NeedUartAux_AreRefused()
        // The CP board module only ships raspberry_pi_pico, so name the pads
        // literally -- busio pins are plain integers anyway.
        => AssertAuxRefused("circuitpython", """
            import busio
            uart = busio.UART(2, 3, baudrate=115200, timeout=0.1, receiver_buffer_size=32)
            """);

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
    public void MpUart_Gp12Gp13_IsAUart0PairOnRp2350()
        => PymcuCompiler.BuildSourceRp2350("""
            from machine import Pin, UART
            u = UART(0, 115200, tx=Pin(12), rx=Pin(13), timeout=100)
            """, "micropython").Should().NotBeEmpty(
            "GP12/GP13 are UART0 pads through plain GPIO_FUNC_UART on the RP2350");

    [Test]
    public void CpUart_Gp12Gp13_IsAUart0PairOnRp2350()
        => PymcuCompiler.BuildSourceRp2350("""
            import busio
            uart = busio.UART(12, 13, baudrate=115200, timeout=0.1, receiver_buffer_size=32)
            """, "circuitpython").Should().NotBeEmpty(
            "GP12/GP13 are a UART0 pair through plain GPIO_FUNC_UART on the RP2350");

    private static void AssertAuxRefused(string stdlib, string source)
    {
        Action build = () => PymcuCompiler.BuildSourceRp2350(source, stdlib);
        build.Should().Throw<InvalidOperationException>().WithMessage("*GPIO_FUNC_UART_AUX*",
            "a pad that only exposes a UART behind UART_AUX must be refused, not silently muxed to a non-UART function");
    }
}
