/* SPDX-License-Identifier: MIT
 * PyMCU RP2350 picobin IMAGE_DEF block.
 *
 * The RP2350 BootROM scans the first 4 KiB of flash for a picobin block and
 * refuses to boot an image without one. This is the minimal 5-word IMAGE_DEF
 * block for an ARM Secure RP2350 executable -- byte-for-byte the block the
 * RP2350Sharp TestKit injects for raw binaries, so an image carrying it boots
 * identically on the emulator (no auto-injection) and is the basis for real HW.
 *
 * Layout:
 *   BLOCK_MARKER_START | IMAGE_TYPE item | LAST item | loop_link | MARKER_END
 *
 * NOTE: loop_link = 0 forms a single-block ring accepted by the emulator's
 * BootROM. A hardware-exact self-relative loop_link is a follow-up.
 */

    .section .picobin, "a", %progbits
    .align 2
    .global __picobin_block
__picobin_block:
    .word 0xFFFFDED3            /* BLOCK_MARKER_START                       */
    .word 0x10210142           /* IMAGE_TYPE: type=0x42 count=1 flags=0x1021 */
    .word 0x000001FF           /* LAST: type=0xFF count=1 pad=0            */
    .word 0x00000000           /* loop_link = 0 (single-block ring)        */
    .word 0xAB123579           /* BLOCK_MARKER_END                         */
    .size __picobin_block, . - __picobin_block
