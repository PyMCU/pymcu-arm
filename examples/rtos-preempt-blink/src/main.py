# Preemptive FreeRTOS-style scheduler in Python (PyMCU). SysTick pends PendSV;
# PendSV swaps task context on PSP. Tasks need NOT yield -- they are preempted.
from pymcu.types import ptr, uint32, naked, Callable
from pymcu.hal.gpio import Pin

# tcb_sp[8] @0x20020000 ; current @0x20020040 ; num_tasks @0x20020044
# RAM vector table @0x20021000 ; task stacks 4 KB each @0x20030000.


def xTaskCreate(entry: Callable):
    idx_p: ptr[uint32] = ptr(0x20020044)
    idx: uint32 = idx_p.value
    base: uint32 = 0x20030000 + (idx + 1) * 0x1000 - 64   # 16-word exception frame
    a: uint32 = entry
    pc_p: ptr[uint32] = ptr(base + 56)                    # PC slot (no Thumb bit in hw frame)
    pc_p.value = a & 0xFFFFFFFE
    xpsr_p: ptr[uint32] = ptr(base + 60)                  # xPSR: Thumb state
    t: uint32 = 0x01000000
    xpsr_p.value = t
    tcb_p: ptr[uint32] = ptr(0x20020000 + idx * 4)
    tcb_p.value = base
    idx_p.value = idx + 1


@naked
def _systick_handler():
    asm("""
        ldr  r0, =0xE000ED04
        movs r1, #1
        lsls r1, r1, #28
        str  r1, [r0]
        bx   lr
    """)


@naked
def _pendsv_handler():
    asm("""
        mrs  r0, psp
        cmp  r0, #0
        beq  .Lrestore
        subs r0, r0, #32
        stmia r0!, {r4-r7}
        mov  r4, r8
        mov  r5, r9
        mov  r6, r10
        mov  r7, r11
        stmia r0!, {r4-r7}
        subs r0, r0, #32
        ldr  r2, =0x20020000
        ldr  r3, =0x20020040
        ldr  r1, [r3]
        lsls r1, r1, #2
        str  r0, [r2, r1]
        ldr  r1, [r3]
        ldr  r5, =0x20020044
        ldr  r5, [r5]
        adds r1, r1, #1
        cmp  r1, r5
        bcc  .Lkeep
        movs r1, #0
    .Lkeep:
        str  r1, [r3]
    .Lrestore:
        ldr  r2, =0x20020000
        ldr  r3, =0x20020040
        ldr  r1, [r3]
        lsls r1, r1, #2
        ldr  r0, [r2, r1]
        ldmia r0!, {r4-r7}
        mov  r8, r4
        mov  r9, r5
        mov  r10, r6
        mov  r11, r7
        ldmia r0!, {r4-r7}
        msr  psp, r0
        ldr  r0, =0xFFFFFFFD
        bx   r0
    """)


def _install_vectors():
    src: uint32 = ptr(0xE000ED08).value          # current VTOR
    dst: uint32 = 0x20021000
    i: uint32 = 0
    while i < 48:
        s: ptr[uint32] = ptr(src + i * 4)
        d: ptr[uint32] = ptr(dst + i * 4)
        d.value = s.value
        i = i + 1
    pend: ptr[uint32] = ptr(dst + 56)            # exception 14 = PendSV
    pend.value = _pendsv_handler
    syst: ptr[uint32] = ptr(dst + 60)            # exception 15 = SysTick
    syst.value = _systick_handler
    ptr(0xE000ED08).value = dst                  # VTOR -> RAM table
    # PendSV lowest priority (SHPR3 byte 2)
    shpr3: ptr[uint32] = ptr(0xE000ED20)
    shpr3.value = 0x00FF0000


@naked
def _start_first():
    asm("""
        movs r0, #0
        msr  psp, r0
        movs r0, #2
        msr  control, r0
        isb
        ldr  r0, =0xE000ED04
        movs r1, #1
        lsls r1, r1, #28
        str  r1, [r0]
        cpsie i
    .Lwait:
        b    .Lwait
    """)


def main():
    n: ptr[uint32] = ptr(0x20020044)
    n.value = 0
    cur: ptr[uint32] = ptr(0x20020040)
    cur.value = 0
    led_a = Pin(24, Pin.OUT)
    led_b = Pin(25, Pin.OUT)
    _install_vectors()
    xTaskCreate(task_a)
    xTaskCreate(task_b)
    # SysTick: reload small for frequent preemption, enable with tickint + processor clk
    rvr: ptr[uint32] = ptr(0xE000E014)
    rvr.value = 0x2000
    cvr: ptr[uint32] = ptr(0xE000E018)
    cvr.value = 0
    csr: ptr[uint32] = ptr(0xE000E010)
    csr.value = 7
    _start_first()
    while True:
        pass


def task_a():
    p: ptr[uint32] = ptr(0xD000001C)             # SIO_GPIO_OUT_XOR
    while True:
        p.value = 1 << 24


def task_b():
    p: ptr[uint32] = ptr(0xD000001C)
    while True:
        p.value = 1 << 25
