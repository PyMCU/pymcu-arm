# asm() with operands on ARM (%N -> LLVM $N, tied read-write) + SIO division
# (now IRQ-safe via PRIMASK) still returning correct results.
#
# Expected UART output:
#   ASM
#   42
#   52
#   13
#   6
from pymcu.types import uint32
from pymcu.hal.uart import UART


def main():
    uart = UART(115200)
    uart.println("ASM")

    a: uint32 = 41
    asm("adds %0, %0, #1", a)
    print(a)

    b: uint32 = 10
    c: uint32 = 42
    asm("adds %0, %0, %1", b, c)
    print(b)

    n: uint32 = 40
    d: uint32 = 3
    q: uint32 = n // d
    print(q)
    r: uint32 = 20
    m: uint32 = r % 7
    print(m)

    while True:
        pass
