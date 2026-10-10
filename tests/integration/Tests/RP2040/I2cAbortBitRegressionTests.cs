using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class I2cAbortBitRegressionTests
{
    // Pins PyMCU/PyMCU: I2C0_IC_RAW_INTR_STAT's TX_ABRT bit was read at bit 9
    // (STOP_DET) instead of bit 6. STOP_DET latches on every transaction that
    // ends in a STOP condition -- virtually all of them -- so write_then_read
    // reported an abort on every ordinary, successful transfer. Any caller
    // that raises OSError on a nonzero abort code (this firmware's own
    // inner(), or busio.I2C.writeto_then_readfrom) then hit an unhandled
    // exception and halted in __pymcu_unhandled_exn before ever printing
    // "DONE" -- not a compiler/codegen bug, a wrong register bit.
    //
    // This only passes once pymcu-stdlib's I2C_RAW_INTR_TX_ABRT is fixed to 6
    // (lib/src/pymcu/chips/rp2040.py in PyMCU). It is added here, alongside
    // the ARM emulator harness that caught it, so a stdlib regression on this
    // constant fails CI the same way the original bug would have.
    [Test]
    public void SuccessfulTransferDoesNotRaise()
    {
        var firmware = PymcuCompiler.BuildRp2040("i2c-chipid-rp2040-raise");
        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        byte? registerWritten = null;
        pico.Rp2040.I2c0.OnWrite = (addr, data) => { if (addr == 0x77) registerWritten = data; };
        pico.Rp2040.I2c0.OnRead = addr => addr == 0x77 ? (byte)0x58 : (byte)0xFF;

        pico.RunUntilOutput(pico.Uart0, "RDY", timeoutMs: 20_000).Should().BeTrue("stage 1: boot");
        pico.RunUntilOutput(pico.Uart0, line => line.Contains("DONE") || line.Contains("E:OSError"),
                timeoutMs: 20_000)
            .Should().BeTrue("the firmware reaches either DONE or the unhandled-exception trap");

        pico.Uart0.Text.Should().NotContain("E:OSError",
            "write_then_read must not report an abort on a transaction the device fully acknowledged");
        pico.Uart0.Text.Should().Contain("DONE",
            "inner() must not raise when rc == I2C_OK, so execution reaches print(\"DONE\")");
        registerWritten.Should().Be(0xD0);
    }
}
