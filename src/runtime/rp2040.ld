/* SPDX-License-Identifier: MIT
 * PyMCU RP2040 linker script.
 *
 * Flash image layout (consumed by RP2040Sharp PicoSimulation.LoadFlash):
 *   0x10000000  .boot2     256-byte stage-2 stub
 *   0x10000100  .vectors   Cortex-M0+ vector table (emulator scans here first)
 *   0x10000140  .text/.rodata, then the .data initialiser image
 *   0x20000000  .data (copied at reset), .bss, stack grows down from _stack_top
 */

MEMORY
{
    FLASH (rx)  : ORIGIN = 0x10000000, LENGTH = 2048K
    RAM   (rwx) : ORIGIN = 0x20000000, LENGTH = 264K
}

/* Full-descending stack starts at the top of SRAM. */
_stack_top = ORIGIN(RAM) + LENGTH(RAM);

/* The GC heap (list[T]/array.array, the AVR-only collector ported to ARM):
 * unlike the arena (RFC 0004, a fixed-size static array PyMCU itself tightly sizes from
 * the program's own bytearray(n) calls), a growing list's peak usage is NOT statically
 * boundable -- that is the entire reason it needs a collector instead of a bump
 * allocator with no free(). So the heap gets everything free between the end of static
 * storage (_ebss, already 4-byte aligned by .bss's own closing ALIGN) and a fixed
 * reservation below the stack, the same shape AVR's own hand-placed heap_end leaves below
 * its hardware call stack (AvrCodeGen.cs, EmitGcSramLayout) -- just with a real linker
 * computing the boundary instead of a literal address, since this target has a real one.
 * 8 KiB is generous against this chip's 264 KiB of RAM; a program with no list[T] never
 * references either symbol, so this costs nothing (pure symbol arithmetic, no section). */
__PYMCU_GC_HEAP_STACK_RESERVE = 8K;
__heap_start = _ebss;
__heap_end = _stack_top - __PYMCU_GC_HEAP_STACK_RESERVE;

ENTRY(_boot2)

SECTIONS
{
    /* Stage-2 boot loader at the very start of flash. */
    .boot2 ORIGIN(FLASH) :
    {
        KEEP(*(.boot2))
    } > FLASH

    /* Vector table must sit at flash + 0x100 (explicit LMA/VMA). */
    .vectors ORIGIN(FLASH) + 0x100 :
    {
        KEEP(*(.vectors))
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
