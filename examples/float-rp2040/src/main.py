# float on RP2040 via the bootrom fast-float library (correctly-rounded IEEE-754
# single precision in ROM; crt0 resolves the SF table and shims __aeabi_f*).
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

    print(d)          # 5.75 -> "5.7" (one-decimal contract, like AVR)
    print(e)          # -2.5 -> "-2.5"

    while True:
        pass
