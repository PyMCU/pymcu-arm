/* SPDX-License-Identifier: MIT
 * PyMCU RP2350 C runtime startup (crt0) + Cortex-M33 (ARMv8-M Mainline) vectors.
 *
 * Layout (see rp2350.ld):
 *   flash + 0x000 : this vector table (.vectors) -- SP = word0, reset = word1
 *   flash + 0x040 : picobin IMAGE_DEF block (picobin_rp2350.S)
 *   flash + ...   : .text / .rodata, then .data initialiser image
 *
 * Reset_Handler initialises the C environment (enable the FPU, copy .data,
 * zero .bss) and calls main(). Single core, no interrupts. Float uses the M33
 * FPU (FPv5-SP) with the soft-float calling convention (softfp): CPACR grants
 * CP10/CP11 full access before any VFP instruction can execute.
 */

    .syntax unified
    .cpu cortex-m33
    .thumb

/* ---- Cortex-M33 (ARMv8-M) vector table (lives at flash + 0) ---- */
    .section .vectors, "a", %progbits
    .align 2
    .global __vectors
__vectors:
    .word _stack_top            /* 0x00  Initial Stack Pointer            */
    .word Reset_Handler         /* 0x04  Reset                            */
    .word Default_Handler       /* 0x08  NMI                              */
    .word Default_Handler       /* 0x0C  HardFault                        */
    .word Default_Handler       /* 0x10  MemManage                        */
    .word Default_Handler       /* 0x14  BusFault                         */
    .word Default_Handler       /* 0x18  UsageFault                       */
    .word Default_Handler       /* 0x1C  SecureFault                      */
    .word 0                     /* 0x20  reserved                         */
    .word 0                     /* 0x24  reserved                         */
    .word 0                     /* 0x28  reserved                         */
    .word Default_Handler       /* 0x2C  SVCall                           */
    .word Default_Handler       /* 0x30  DebugMon                         */
    .word 0                     /* 0x34  reserved                         */
    .word Default_Handler       /* 0x38  PendSV                           */
    .word Default_Handler       /* 0x3C  SysTick                          */
    .size __vectors, . - __vectors

/* ---- Reset handler ---- */
    .section .text._reset, "ax", %progbits
    .align 2
    .global Reset_Handler
    .type Reset_Handler, %function
    .thumb_func
Reset_Handler:
    /* Enable the FPU: CPACR (0xE000ED88) CP10/CP11 = full access (0b11 each).
     * Must happen before any VFP instruction; DSB+ISB flush the pipeline so
     * the grant is visible to the instructions that follow. */
    ldr  r0, =0xE000ED88
    ldr  r1, [r0]
    orr  r1, r1, #(0xF << 20)
    str  r1, [r0]
    dsb
    isb

    /* Copy .data from its flash load address (_sidata) to SRAM (_sdata.._edata). */
    ldr  r0, =_sdata
    ldr  r1, =_edata
    ldr  r2, =_sidata
.Lcopy:
    cmp  r0, r1
    bhs  .Lcopy_done
    ldr  r3, [r2]
    str  r3, [r0]
    adds r0, r0, #4
    adds r2, r2, #4
    b    .Lcopy
.Lcopy_done:

    /* Zero .bss (_sbss.._ebss). */
    ldr  r0, =_sbss
    ldr  r1, =_ebss
    movs r3, #0
.Lbss:
    cmp  r0, r1
    bhs  .Lbss_done
    str  r3, [r0]
    adds r0, r0, #4
    b    .Lbss
.Lbss_done:

    bl   main
.Lhang:
    b    .Lhang
    .ltorg
    .size Reset_Handler, . - Reset_Handler

/* ---- Default handler: trap (spin) ---- */
    .section .text._default, "ax", %progbits
    .global Default_Handler
    .type Default_Handler, %function
    .thumb_func
Default_Handler:
    b    Default_Handler
    .size Default_Handler, . - Default_Handler
