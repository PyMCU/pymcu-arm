namespace PyMCU.IntegrationTests;

/// <summary>
/// The RP2040 byte that stands in for the AVR's GPIOR0 spare register in the probes that
/// read it (oracle probes 272, 294, 511, 536 and the others that adapt GPIOR0 the same way).
/// </summary>
/// <remarks>
/// It is WATCHDOG_SCRATCH0, not a byte of SRAM. The probes assume the byte reads as zero when
/// the program starts, and the datasheet says so for this one: WATCHDOG_SCRATCH0 is RW with
/// reset value 0x00000000 (pico-sdk, hardware/regs/watchdog.h). No PyMCU code writes it: the
/// runtime, the HAL and the stdlib leave the watchdog alone, and the tests only read it.
///
/// An SRAM byte cannot give that guarantee. The stack grows down from the top of RAM, so a byte
/// 16 bytes below the top is the first one a deep call chain writes, and what it holds depends on
/// the frame of main. With the byte there, FloatArrayElementTests failed with an IndexError, because
/// the probe indexes a float array with the scratch value and it was not 0.
///
/// The scratch registers keep their value through a soft reset of the chip, so on silicon the byte
/// is zero only after a power-on. The emulator starts every test from a fresh machine.
/// </remarks>
public static class Rp2040Scratch
{
    /// <summary>WATCHDOG base 0x40058000 + SCRATCH0 offset 0x0C.</summary>
    public const string GPIOR0Address = "0x4005800C";
}
