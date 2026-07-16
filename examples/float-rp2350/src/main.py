# float on RP2350 via the Cortex-M33 FPU (FPv5-SP, softfp: VFP instructions
# with the soft-float calling convention; crt0_m33 enables CPACR at reset).
# Mirrors the float-rp2040 fixture, which lowers the same source to __aeabi_f*
# shims over the RP2040 bootrom fast-float library.
#
# 2.5*4.0+1.5 = 11.5; 11.5/2.0 = 5.75 > 5.0 -> "G"; int(5.75*10.0) = 57;
# int(-2.5) = -2 (toward zero); 7.0 // 2.0 -> 3 (int dst converts toward zero).
#
# Expected UART output:
#   FLT
#   G
#   57
#   -2
#   3
#   5.7
#   -2.5
from pymcu.types import uint16, int16
from pymcu.hal.uart import UART


def main():
    uart = UART(115200)
    uart.println("FLT")

    a: float = 2.5
    b: float = 4.0
    c: float = a * b + 1.5
    d: float = c / 2.0
    if d > 5.0:
        uart.println("G")

    n: uint16 = int(d * 10.0)
    print(n)

    e: float = -2.5
    m: int16 = int(e)
    print(m)

    q: uint16 = int(7.0 / 2.0)
    print(q)

    print(d)          # 5.75 -> "5.7" (one-decimal contract, like AVR/RP2040)
    print(e)          # -2.5 -> "-2.5"

    while True:
        pass
