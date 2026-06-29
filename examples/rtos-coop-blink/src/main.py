# A cooperative FreeRTOS-style scheduler written in Python, compiled by PyMCU.
# Kernel state lives at fixed RAM (0x20020000); each task has its own stack;
# the context switch is a naked asm SP-swap. Tasks call taskYIELD() to hand off.
from pymcu.types import ptr, uint32, naked, Callable
from pymcu.hal.gpio import Pin

# tcb_sp[8] @ 0x20020000 ; current @ 0x20020040 ; num_tasks @ 0x20020044
# task stacks: 4 KB each from 0x20030000.


def xTaskCreate(entry: Callable):
    idx_p: ptr[uint32] = ptr(0x20020044)
    idx: uint32 = idx_p.value
    sp: uint32 = 0x20030000 + (idx + 1) * 0x1000 - 36
    pc_p: ptr[uint32] = ptr(sp + 32)
    pc_p.value = entry                       # PC slot (FunctionRef -> addr|Thumb)
    tcb_p: ptr[uint32] = ptr(0x20020000 + idx * 4)
    tcb_p.value = sp
    idx_p.value = idx + 1


@naked
def taskYIELD():
    asm("""
        push {r4-r7, lr}
        mov  r4, r8
        mov  r5, r9
        mov  r6, r10
        mov  r7, r11
        push {r4-r7}
        ldr  r2, =0x20020000
        ldr  r3, =0x20020040
        ldr  r0, [r3]
        lsls r1, r0, #2
        mov  r4, sp
        str  r4, [r2, r1]
        ldr  r5, =0x20020044
        ldr  r5, [r5]
        adds r0, r0, #1
        cmp  r0, r5
        bcc  1f
        movs r0, #0
    1:
        str  r0, [r3]
        lsls r1, r0, #2
        ldr  r4, [r2, r1]
        mov  sp, r4
        pop  {r4-r7}
        mov  r8, r4
        mov  r9, r5
        mov  r10, r6
        mov  r11, r7
        pop  {r4-r7, pc}
    """)


@naked
def vTaskStartScheduler():
    asm("""
        ldr  r2, =0x20020000
        ldr  r3, =0x20020040
        movs r0, #0
        str  r0, [r3]
        ldr  r4, [r2]
        mov  sp, r4
        pop  {r4-r7}
        mov  r8, r4
        mov  r9, r5
        mov  r10, r6
        mov  r11, r7
        pop  {r4-r7, pc}
    """)


def task_a():
    p: ptr[uint32] = ptr(0xD000001C)         # SIO_GPIO_OUT_XOR
    while True:
        p.value = 1 << 24
        taskYIELD()


def task_b():
    p: ptr[uint32] = ptr(0xD000001C)
    while True:
        p.value = 1 << 25
        taskYIELD()


def main():
    a: ptr[uint32] = ptr(0x20020044)
    a.value = 0                              # num_tasks = 0
    led_a = Pin(24, Pin.OUT)
    led_b = Pin(25, Pin.OUT)
    xTaskCreate(task_a)
    xTaskCreate(task_b)
    vTaskStartScheduler()
    while True:
        pass
