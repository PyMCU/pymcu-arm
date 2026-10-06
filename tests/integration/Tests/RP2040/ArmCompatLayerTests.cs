using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// Compile-time diagnostics and on-silicon behaviour of the ARM branches of the two
/// compat layers (busio.UART in CircuitPython, machine.Pin/machine.UART in MicroPython).
/// Every refusal here names the unsupported shape and the supported spelling instead,
/// because a compat layer that silently approximates reports wrong data as if it worked.
/// </summary>
[TestFixture]
public class ArmCompatLayerTests
{
    private const string Pico = "raspberry_pi_pico";

    // ------------------------------------------------------------------ helpers

    private static void Refused(string source, string stdlib, string fragment,
        string? board = null)
    {
        Action build = () => PymcuCompiler.BuildSourceRp2040(source, stdlib, board);
        build.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{fragment}*");
    }

    // ------------------------------------------------------------ busio.UART pins

    [Test]
    public void CpUart_NoPins_IsRefused()
        => Refused("import board\nimport busio\nbusio.UART()", "circuitpython",
            "tx and rx cannot both be None", Pico);

    [Test]
    public void CpUart_TxOnly_IsRefused()
        => Refused("import board\nimport busio\nbusio.UART(board.TX)", "circuitpython",
            "tx-only or rx-only", Pico);

    [Test]
    public void CpUart_RxOnly_IsRefused()
        => Refused("import board\nimport busio\nbusio.UART(rx=board.RX)", "circuitpython",
            "tx-only or rx-only", Pico);

    // ------------------------------------------------------------- busio.UART API

    [Test]
    public void CpUart_InWaiting_IsRefused()
        => Refused("""
            import board
            import busio
            uart = busio.UART(board.TX, board.RX, receiver_buffer_size=32)
            w = uart.in_waiting
            """, "circuitpython", "in_waiting", Pico);

    [Test]
    public void CpUart_Readinto_AnswersNoneOnTimeout()
    {
        // CircuitPython's readinto answers None when the timeout passes with no
        // byte; the ARM port used to answer 0, and timeout=0.1 used to truncate to
        // 0 ms. This program reports which spelling actually came back.
        var firmware = PymcuCompiler.BuildSourceRp2040("""
            import board
            import busio
            def main():
                uart = busio.UART(board.TX, board.RX, baudrate=115200, timeout=0.1, receiver_buffer_size=32)
                uart.write("RDY\n")
                buf = bytearray(4)
                n = uart.readinto(buf)
                if n is None:
                    uart.write("NONE\n")
                else:
                    uart.write("SOME\n")
            """, "circuitpython", Pico);
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "NONE", timeoutMs: 20_000)
            .Should().BeTrue("readinto must answer None (not 0) when the timeout passes with no byte");
    }

    [Test]
    public void CpUart_Readinto_CountsAPartialBurst()
    {
        // Two bytes into a four-byte buffer: readinto returns the real count, 2.
        var firmware = PymcuCompiler.BuildSourceRp2040("""
            import board
            import busio
            def main():
                uart = busio.UART(board.TX, board.RX, baudrate=115200, timeout=2.0, receiver_buffer_size=32)
                buf = bytearray(4)
                uart.write("RDY\n")
                n = uart.readinto(buf)
                if n is None:
                    uart.write("NONE\n")
                elif n == 2:
                    uart.write("TWO\n")
                else:
                    uart.write("OTHER\n")
            """, "circuitpython", Pico);
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "RDY", timeoutMs: 20_000);
        pico.Uart0.InjectByte(0x41);
        pico.Uart0.InjectByte(0x5A);
        pico.RunUntilOutput(pico.Uart0, "TWO", timeoutMs: 20_000)
            .Should().BeTrue("readinto must report the real count on a two-byte burst");
    }

    [Test]
    public void CpUart_Timeout_Seconds_WindowIsReal()
    {
        // timeout=5.0 gives a 5 s receive window: a byte injected after the banner
        // must still be inside it. Under the old ms reading (5 ms) or the truncation
        // to 0, the window is long gone and the program reports NONE.
        var firmware = PymcuCompiler.BuildSourceRp2040("""
            import board
            import busio
            def main():
                uart = busio.UART(board.TX, board.RX, baudrate=115200, timeout=5.0, receiver_buffer_size=32)
                buf = bytearray(4)
                uart.write("RDY\n")
                n = uart.readinto(buf)
                if n is None:
                    uart.write("NONE\n")
                elif n == 1:
                    uart.write("GOT\n")
                else:
                    uart.write("OTHER\n")
            """, "circuitpython", Pico);
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "RDY", timeoutMs: 20_000);
        pico.Uart0.InjectByte(0x41);
        pico.RunUntilOutput(pico.Uart0, "GOT", timeoutMs: 20_000)
            .Should().BeTrue("a byte arriving inside the 5 s window must be read (float timeout is seconds)");
    }

    [Test]
    public void CpUart_Timeout_IntIsSeconds_Too()
    {
        // CircuitPython feeds every number through mp_obj_get_float: timeout=1
        // is a one-second window, not one millisecond. A byte injected after
        // the banner must land inside it; under ms semantics the read is long
        // over and the program reports NONE.
        var firmware = PymcuCompiler.BuildSourceRp2040("""
            import board
            import busio
            def main():
                uart = busio.UART(board.TX, board.RX, baudrate=115200, timeout=1, receiver_buffer_size=32)
                buf = bytearray(4)
                uart.write("RDY\n")
                n = uart.readinto(buf)
                if n is None:
                    uart.write("NONE\n")
                elif n == 1:
                    uart.write("GOT\n")
                else:
                    uart.write("OTHER\n")
            """, "circuitpython", Pico);
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "RDY", timeoutMs: 20_000);
        pico.RunMilliseconds(500);
        pico.Uart0.InjectByte(0x41);
        pico.RunUntilOutput(pico.Uart0, "GOT", timeoutMs: 20_000)
            .Should().BeTrue("timeout=1 is one second, so a byte at ~500 ms must be read");
    }

    [Test]
    public void CpUart_Timeout_FloatAndSetter_Compile()
    {
        // Every numeric spelling is seconds (constructor and setter alike),
        // as CircuitPython defines it. Both must compile.
        PymcuCompiler.BuildSourceRp2040("""
            import board
            import busio
            def main():
                uart = busio.UART(board.TX, board.RX, timeout=0.1, receiver_buffer_size=32)
                uart.timeout = 0.05
                uart.timeout = 30
                uart.write("OK\n")
            """, "circuitpython", Pico).Should().NotBeEmpty();
    }

    [Test]
    public void CpUart_FloatTimeout_TooBig_IsRefused()
        => Refused("""
            import board
            import busio
            busio.UART(board.TX, board.RX, timeout=66.0, receiver_buffer_size=32)
            """, "circuitpython", "65.535", Pico);

    [Test]
    public void CpUart_IntTimeout_TooBig_IsRefused()
        => Refused("""
            import board
            import busio
            busio.UART(board.TX, board.RX, timeout=66, receiver_buffer_size=32)
            """, "circuitpython", "65.535", Pico);

    // -------------------------------------------------------- busio.UART pad routing

    [Test]
    public void CpUart_Uart1Pads_AreRefused()
    {
        // GP4/GP5 are UART1's pair on an RP2040. CircuitPython would pick the
        // controller from the pins; this layer's HAL drives UART0 only, so the
        // pair must be refused, never silently routed through UART0.
        Refused("""
            import board
            import busio
            busio.UART(board.GP4, board.GP5, baudrate=115200, receiver_buffer_size=32)
            """, "circuitpython", "UART0 only", Pico);
    }

    [Test]
    public void CpUart_MismatchedPair_IsRefused()
        => Refused("""
            import board
            import busio
            busio.UART(board.GP0, board.GP5, baudrate=115200, receiver_buffer_size=32)
            """, "circuitpython", "different UARTs", Pico);

    [Test]
    public void CpUart_BadTxPad_IsRefused()
        => Refused("""
            import board
            import busio
            busio.UART(board.GP2, board.GP3, baudrate=115200, receiver_buffer_size=32)
            """, "circuitpython", "transmit pad", Pico);

    [Test]
    public void CpUart_BadRxPad_IsRefused()
        => Refused("""
            import board
            import busio
            busio.UART(board.GP0, board.GP2, baudrate=115200, receiver_buffer_size=32)
            """, "circuitpython", "receive pad", Pico);

    [Test]
    public void CpUart_AltUart0Pair_Works()
    {
        // GP16/GP17 are UART0's second pair on an RP2040: a byte through them
        // still comes out of UART0.
        var firmware = PymcuCompiler.BuildSourceRp2040("""
            import board
            import busio
            def main():
                uart = busio.UART(board.GP16, board.GP17, baudrate=115200, timeout=2.0, receiver_buffer_size=32)
                buf = bytearray(4)
                uart.write("RDY\n")
                n = uart.readinto(buf)
                if n is not None:
                    uart.write("GOT\n")
            """, "circuitpython", Pico);
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "RDY", timeoutMs: 20_000);
        pico.Uart0.InjectByte(0x41);
        pico.RunUntilOutput(pico.Uart0, "GOT", timeoutMs: 20_000)
            .Should().BeTrue("GP16/GP17 are a UART0 pair, so the read must work");
    }

    // -------------------------------------------------------- machine.UART pads

    [Test]
    public void MpUart_BadPinRoles_AreRefused()
    {
        // Pin(1) is an RX pad and Pin(0) a TX pad: upstream's IS_VALID_TX/RX
        // says this pair can never back UART(0).
        Refused("""
            from machine import Pin, UART
            u = UART(0, 115200, tx=Pin(1), rx=Pin(0))
            """, "micropython", "TX", Pico);
    }

    [Test]
    public void MpUart_Uart1Pads_AreRefused()
    {
        // GP4/GP5 are UART1's pair; this HAL drives UART0 only.
        Refused("""
            from machine import Pin, UART
            u = UART(0, 115200, tx=Pin(4), rx=Pin(5))
            """, "micropython", "UART0", Pico);
    }

    [Test]
    public void MpUart_AltUart0Pair_Works()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040("""
            from machine import Pin, UART
            def main():
                u = UART(0, 115200, tx=Pin(16), rx=Pin(17), timeout=2000)
                buf = bytearray(1)
                u.write("RDY\\n")
                n = u.readinto(buf)
                if n is not None:
                    u.write("GOT\\n")
            """, "micropython", Pico);
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "RDY", timeoutMs: 20_000);
        pico.Uart0.InjectByte(0x41);
        pico.RunUntilOutput(pico.Uart0, "GOT", timeoutMs: 20_000)
            .Should().BeTrue("GP16/GP17 are UART0's second pair on an RP2040");
    }

    // -------------------------------------------------------- machine.UART.init

    [Test]
    public void MpUart_BareInit_KeepsTheRunningConfig()
    {
        // Upstream init() with no arguments leaves everything unchanged: the
        // UART stays at the constructor's baud, not the HAL default 9600.
        // 57600/125 MHz lands on IBRD=135 (the integer divisor), so check it.
        var firmware = PymcuCompiler.BuildSourceRp2040("""
            from machine import Pin, UART
            u = UART(0, 57600, tx=Pin(16), rx=Pin(17), timeout=100)
            def main():
                u.init()
                u.write("RDY\\n")
            """, "micropython", Pico);
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "RDY", timeoutMs: 20_000).Should().BeTrue();
        pico.Rp2040.Uart0.GetConfig().Ibrd.Should().Be(135,
            "bare init() must not rebuild the UART at 9600 (IBRD 867)");
    }

    [Test]
    public void MpUart_PartialInit_IsRefused()
    {
        // The stored frame is runtime state that cannot be re-read as the
        // compile-time constants the HAL constructor folds from, so a partial
        // re-init is refused rather than silently resetting to defaults.
        Refused("""
            from machine import Pin, UART
            u = UART(0, 57600, tx=Pin(16), rx=Pin(17))
            u.init(9600)
            """, "micropython", "baudrate, bits, parity and stop", Pico);
    }

    [Test]
    public void MpUart_FullInit_ReprogramsTheUart()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040("""
            from machine import Pin, UART
            u = UART(0, 115200, tx=Pin(16), rx=Pin(17), timeout=100)
            def main():
                u.init(57600, 8, None, 1, tx=Pin(16), rx=Pin(17))
                u.write("RDY\\n")
            """, "micropython", Pico);
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "RDY", timeoutMs: 20_000).Should().BeTrue();
        pico.Rp2040.Uart0.GetConfig().Ibrd.Should().Be(135, "init with a full frame must reprogram the baud");
    }

    [Test]
    public void MpUart_InitWithBadPads_IsRefused()
        => Refused("""
            from machine import Pin, UART
            u = UART(0, 115200, tx=Pin(16), rx=Pin(17))
            u.init(115200, 8, None, 1, tx=Pin(4), rx=Pin(5))
            """, "micropython", "bad TX pin", Pico);

    // -------------------------------------------------------- machine.Pin open-drain

    [Test]
    public void MpPin_OpenDrain_Ctor_IsRefused()
        => Refused("from machine import Pin\np = Pin(4, Pin.OPEN_DRAIN)", "micropython",
            "OPEN_DRAIN");

    [Test]
    public void MpPin_OpenDrain_Init_IsRefused()
        => Refused("""
            from machine import Pin
            p = Pin(4, Pin.IN)
            p.init(mode=Pin.OPEN_DRAIN)
            """, "micropython", "OPEN_DRAIN");

    // ----------------------------------------------------------- machine.Pin pull

    [Test]
    public void MpPin_PullNone_ClearsBothPulls()
    {
        // pull=None is upstream's "disable pulls" spelling; PADS resets with the
        // pull-down enabled, so the fix must show up as PDE+PUE clear on the pad.
        // The pull is a positional argument: @inline overloads dispatch on
        // positional types, so pull= cannot name it.
        var firmware = PymcuCompiler.BuildSourceRp2040("""
            from machine import Pin
            def main():
                p = Pin(6, Pin.IN, None)
                q = Pin(7, Pin.IN)
                q.init(pull=None)
                while True:
                    pass
            """, "micropython");
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunMilliseconds(5);

        foreach (var pad in new[] { 6u, 7u })
        {
            var reg = pico.Rp2040.PadsBank0.ReadWord(4 + 4 * pad);
            (reg & (1u << 2)).Should().Be(0, $"GP{pad}: pull=None must clear PDE");
            (reg & (1u << 3)).Should().Be(0, $"GP{pad}: pull=None must clear PUE");
        }
    }

    [Test]
    public void MpPin_PullUp_SetsPue()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040("""
            from machine import Pin
            def main():
                p = Pin(6, Pin.IN, Pin.PULL_UP)
                while True:
                    pass
            """, "micropython");
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunMilliseconds(5);

        var reg = pico.Rp2040.PadsBank0.ReadWord(4 + 4 * 6u);
        (reg & (1u << 3)).Should().NotBe(0, "pull=Pin.PULL_UP must set PUE");
        (reg & (1u << 2)).Should().Be(0, "pull=Pin.PULL_UP must clear PDE");
    }

    // -------------------------------------------------------- machine.UART streams

    [Test]
    public void MpUart_Read_NoArgs_IsRefused()
        => Refused("""
            from machine import UART
            u = UART(0, 115200)
            b = u.read()
            """, "micropython", "readinto");

    [Test]
    public void MpUart_Any_IsRefused()
        => Refused("""
            from machine import UART
            u = UART(0, 115200)
            n = u.any()
            """, "micropython", "cannot count");

    [Test]
    public void MpUart_Readinto_AnswersNoneWhenEmpty()
    {
        var firmware = PymcuCompiler.BuildSourceRp2040("""
            from machine import UART
            def main():
                u = UART(0, 115200)
                u.write("RDY\n")
                buf = bytearray(4)
                n = u.readinto(buf)
                if n is None:
                    u.write("NONE\n")
                else:
                    u.write("SOME\n")
            """, "micropython");
        var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);
        pico.RunUntilOutput(pico.Uart0, "NONE", timeoutMs: 20_000)
            .Should().BeTrue("machine.UART.readinto must answer None when nothing arrived");
    }
}
