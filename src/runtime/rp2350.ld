/* SPDX-License-Identifier: MIT
 * PyMCU RP2350 (Raspberry Pi Pico 2, Cortex-M33) linker script.
 *
 * Unlike the RP2040 there is no 256-byte boot2 stub: the RP2350 BootROM brings
 * up XIP/clocks itself, then scans the first 4 KiB of flash for a picobin
 * IMAGE_DEF block and boots the vector table at the image start (flash + 0).
 *
 * Flash image layout (consumed by RP2350Sharp PicoSimulation / WithBinary):
 *   0x10000000  .vectors   Cortex-M33 vector table (SP = word0, reset = word1)
 *   0x10000040  .picobin   picobin IMAGE_DEF block (within the first 0x100)
 *   0x10000...  .text/.rodata, then the .data initialiser image
 *   0x20000000  .data (copied at reset), .bss, stack grows down from _stack_top
 */

MEMORY
{
    FLASH (rx)  : ORIGIN = 0x10000000, LENGTH = 4096K
    RAM   (rwx) : ORIGIN = 0x20000000, LENGTH = 520K
}

/* Full-descending stack starts at the top of SRAM. */
_stack_top = ORIGIN(RAM) + LENGTH(RAM);

ENTRY(Reset_Handler)

SECTIONS
{
    /* Vector table at the very start of flash (offset 0): the BootROM uses
     * word[0] as the initial SP and word[1] as the reset vector. */
    .vectors ORIGIN(FLASH) :
    {
        KEEP(*(.vectors))
    } > FLASH

    /* picobin IMAGE_DEF block: must be found within the first 4 KiB scan window
     * (kept right after the vectors, inside flash[0..0x100)). */
    .picobin :
    {
        . = ALIGN(4);
        KEEP(*(.picobin))
    } > FLASH

    .text :
    {
        *(.text._reset)
        *(.text*)
        *(.rodata*)
        . = ALIGN(4);
        _etext = .;
    } > FLASH

    /* .data: virtual in RAM, load image (LMA) in flash after .text. */
    _sidata = LOADADDR(.data);
    .data :
    {
        . = ALIGN(4);
        _sdata = .;
        *(.data*)
        . = ALIGN(4);
        _edata = .;
    } > RAM AT> FLASH

    .bss (NOLOAD) :
    {
        . = ALIGN(4);
        _sbss = .;
        *(.bss*)
        *(COMMON)
        . = ALIGN(4);
        _ebss = .;
    } > RAM

    /DISCARD/ :
    {
        *(.ARM.exidx*)
        *(.ARM.extab*)
        *(.comment)
    }
}
