/* SPDX-License-Identifier: MIT
 * PyMCU RP2040 C runtime startup (crt0) + Cortex-M0+ vector table.
 *
 * Layout (see rp2040.ld):
 *   flash + 0x000 : boot2 stub (boot2.S)
 *   flash + 0x100 : this vector table (.vectors) -- found by the emulator
 *   flash + ...   : .text / .rodata, then .data initialiser image
 *
 * Reset_Handler initialises the C environment (copy .data, zero .bss), sets up
 * nothing else (single core, no interrupts in the MVP), and calls main().
 */

    .syntax unified
    .cpu cortex-m0plus
    .thumb

/* ---- Cortex-M0+ vector table (must live at flash+0x100) ---- */
    .section .vectors, "a", %progbits
    .align 2
    .global __vectors
__vectors:
    .word _stack_top            /* 0x00  Initial Stack Pointer            */
    .word Reset_Handler         /* 0x04  Reset (thumb bit set by .thumb_func) */
    .word Default_Handler       /* 0x08  NMI                              */
    .word Default_Handler       /* 0x0C  HardFault                        */
    .word Default_Handler       /* 0x10  MemManage (reserved on M0+)      */
    .word Default_Handler       /* 0x14  BusFault  (reserved on M0+)      */
    .word Default_Handler       /* 0x18  UsageFault(reserved on M0+)      */
    .word 0                     /* 0x1C  reserved                         */
    .word 0                     /* 0x20  reserved                         */
    .word 0                     /* 0x24  reserved                         */
    .word 0                     /* 0x28  reserved                         */
    .word Default_Handler       /* 0x2C  SVCall                           */
    .word Default_Handler       /* 0x30  DebugMon (reserved on M0+)       */
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

    bl   __pymcu_float_init
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

/* ---- Integer division (Cortex-M0+ has no hardware divide instruction) ----
 * LLVM lowers `/` and `%` to __aeabi_uidiv/__aeabi_uidivmod (and the signed
 * variants). libgcc/compiler-rt is not linked, so provide them here, routed to
 * the RP2040 SIO hardware divider (8-cycle, base 0xD0000000):
 *   UDIVIDEND +0x60  UDIVISOR +0x64  SDIVIDEND +0x68  SDIVISOR +0x6C
 *   QUOTIENT  +0x70  REMAINDER +0x74
 * IRQ-SAFE: the divider state is shared, so interrupts are masked (PRIMASK)
 * around the operate-and-read window -- an ISR (or the RTOS context switch it
 * pends) dividing mid-flight would otherwise corrupt the outer quotient.
 * Costs ~4 cycles per division over the raw 8-cycle divide.
 */
    .section .text.__aeabi_div, "ax", %progbits
    .align 2
    .global __aeabi_uidiv
    .global __aeabi_uidivmod
    .thumb_func
__aeabi_uidiv:
    .thumb_func
__aeabi_uidivmod:
    ldr  r2, =0xd0000000
    mrs  r3, PRIMASK            @ save interrupt mask state
    cpsid i                     @ divider critical section begins
    str  r0, [r2, #0x60]        @ DIV_UDIVIDEND
    str  r1, [r2, #0x64]        @ DIV_UDIVISOR
    nop
    nop
    nop
    nop
    nop
    nop
    nop
    nop
    ldr  r1, [r2, #0x74]        @ DIV_REMAINDER (read first)
    ldr  r0, [r2, #0x70]        @ DIV_QUOTIENT
    msr  PRIMASK, r3            @ restore interrupt mask state
    bx   lr

    .align 2
    .global __aeabi_idiv
    .global __aeabi_idivmod
    .thumb_func
__aeabi_idiv:
    .thumb_func
__aeabi_idivmod:
    ldr  r2, =0xd0000000
    mrs  r3, PRIMASK            @ save interrupt mask state
    cpsid i                     @ divider critical section begins
    str  r0, [r2, #0x68]        @ DIV_SDIVIDEND
    str  r1, [r2, #0x6c]        @ DIV_SDIVISOR
    nop
    nop
    nop
    nop
    nop
    nop
    nop
    nop
    ldr  r1, [r2, #0x74]        @ DIV_REMAINDER
    ldr  r0, [r2, #0x70]        @ DIV_QUOTIENT
    msr  PRIMASK, r3            @ restore interrupt mask state
    bx   lr
    .ltorg


/* ---- Floating point via the RP2040 bootrom fast-float library (mufplib) ----
 * LLVM (soft-float ABI) lowers f32 arithmetic to __aeabi_f* libcalls. The RP2040
 * bootrom ships a correctly-rounded IEEE-754 single-precision library in ROM;
 * __pymcu_float_init resolves its function table once at reset
 * (rom_data_lookup('S','F') -> sf_table) and the shims below tail-jump through it.
 * Table offsets per the datasheet / pico-sdk sf_table.h (V1 bootrom subset used:
 * fadd 0x00, fsub 0x04, fmul 0x08, fdiv 0x0c, fcmp_fast 0x10 -> {-1,0,1},
 * float2int 0x1c, float2uint 0x24 (both round toward -inf), int2float 0x2c,
 * uint2float 0x34). float2int/uint round toward -inf, so the toward-zero AEABI
 * conversions negate negative inputs first.
 */
    .section .bss.__pymcu_sf_table
    .align 2
__pymcu_sf_table:
    .word 0

    .section .text.__pymcu_float, "ax", %progbits
    .align 2
    .global __pymcu_float_init
    .thumb_func
__pymcu_float_init:
    movs r0, #0x16              @ halfword ptr to the ROM data table
    ldrh r0, [r0]
    ldr  r1, =0x4653            @ 'S' | ('F' << 8)
    movs r3, #0x18              @ halfword ptr to rom_table_lookup
    ldrh r3, [r3]
    push {lr}
    blx  r3                     @ r0 = sf_table
    ldr  r2, =__pymcu_sf_table
    str  r0, [r2]
    pop  {pc}

.macro SF_SHIM name, off
    .align 2
    .global \name
    .thumb_func
\name:
    ldr  r2, =__pymcu_sf_table
    ldr  r2, [r2]
    ldr  r2, [r2, #\off]
    bx   r2
.endm

SF_SHIM __aeabi_fadd, 0x00
SF_SHIM __aeabi_fsub, 0x04
SF_SHIM __aeabi_fmul, 0x08
SF_SHIM __aeabi_fdiv, 0x0c
SF_SHIM __aeabi_i2f,  0x2c
SF_SHIM __aeabi_ui2f, 0x34

    /* fcmp_fast: r0 = -1 (a<b), 0 (a==b), 1 (a>b). */
    .align 2
    .thumb_func
__pymcu_fcmp:
    ldr  r2, =__pymcu_sf_table
    ldr  r2, [r2]
    ldr  r2, [r2, #0x10]
    bx   r2

    .align 2
    .global __aeabi_fcmplt
    .thumb_func
__aeabi_fcmplt:                 @ 1 iff a < b  (fcmp == -1: sign bit set)
    push {lr}
    bl   __pymcu_fcmp
    lsrs r0, r0, #31
    pop  {pc}

    .align 2
    .global __aeabi_fcmpgt
    .thumb_func
__aeabi_fcmpgt:                 @ 1 iff a > b  (fcmp == 1)
    push {lr}
    bl   __pymcu_fcmp
    cmp  r0, #1
    beq  1f
    movs r0, #0
    pop  {pc}
1:  movs r0, #1
    pop  {pc}

    .align 2
    .global __aeabi_fcmpeq
    .thumb_func
__aeabi_fcmpeq:                 @ 1 iff a == b (fcmp == 0)
    push {lr}
    bl   __pymcu_fcmp
    cmp  r0, #0
    beq  1f
    movs r0, #0
    pop  {pc}
1:  movs r0, #1
    pop  {pc}

    .align 2
    .global __aeabi_fcmple
    .thumb_func
__aeabi_fcmple:                 @ 1 iff a <= b (fcmp <= 0)
    push {lr}
    bl   __pymcu_fcmp
    cmp  r0, #1
    beq  1f
    movs r0, #1
    pop  {pc}
1:  movs r0, #0
    pop  {pc}

    .align 2
    .global __aeabi_fcmpge
    .thumb_func
__aeabi_fcmpge:                 @ 1 iff a >= b (fcmp >= 0: not -1)
    push {lr}
    bl   __pymcu_fcmp
    lsrs r0, r0, #31
    movs r1, #1
    eors r0, r1
    pop  {pc}

    .align 2
    .global __aeabi_fcmpun
    .thumb_func
__aeabi_fcmpun:                 @ mufplib has no NaN handling: never unordered
    movs r0, #0
    bx   lr

    /* float -> int32 toward zero: ROM float2int rounds toward -inf, identical for
     * non-negative inputs; negate negative inputs around the conversion. */
    .align 2
    .global __aeabi_f2iz
    .thumb_func
__aeabi_f2iz:
    push {lr}
    lsrs r1, r0, #31
    beq  1f
    movs r1, #1
    lsls r1, r1, #31
    eors r0, r1                 @ |x|
    bl   __pymcu_f2i
    rsbs r0, r0, #0             @ negate (v6M: rsbs rd, rn, #0)
    pop  {pc}
1:  bl   __pymcu_f2i
    pop  {pc}

    .align 2
    .thumb_func
__pymcu_f2i:
    ldr  r2, =__pymcu_sf_table
    ldr  r2, [r2]
    ldr  r2, [r2, #0x1c]
    bx   r2

    /* float -> uint32 toward zero (negative inputs: 0). */
    .align 2
    .global __aeabi_f2uiz
    .thumb_func
__aeabi_f2uiz:
    lsrs r1, r0, #31
    beq  1f
    movs r0, #0
    bx   lr
1:  ldr  r2, =__pymcu_sf_table
    ldr  r2, [r2]
    ldr  r2, [r2, #0x24]
    bx   r2
    .ltorg
