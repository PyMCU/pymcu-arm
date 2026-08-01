# irq-critical: enable_interrupts() / disable_interrupts() on ARM lower to the
# Cortex-M PRIMASK instructions (CPSID I / CPSIE I). Before the arm case existed
# in pymcu.hal.irq they folded away to nothing, so every critical section on
# RP2040/RP2350 was silently unprotected.
#
# Expected UART output:
#   IRQ
#   C:3
#   OK
from pymcu.types import uint32
from pymcu.hal.uart import UART
from pymcu.hal.irq import enable_interrupts, disable_interrupts

counter: uint32 = 0


def bump():
    global counter
    disable_interrupts()
    counter = counter + 1
    enable_interrupts()


def main():
    uart = UART(115200)
    uart.println("IRQ")

    enable_interrupts()
    bump()
    bump()
    bump()
    print(f"C:{counter}")

    uart.println("OK")
    while True:
        pass
