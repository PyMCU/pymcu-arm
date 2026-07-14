# fstring-value: f-strings as VALUES -- built into a fixed buffer, no heap.
#
# `s = f"..."` with runtime interpolations lowers to a compiler-managed
# bytearray plus pymcu.strfmt calls. Consumers: print(s), uart.write_str(s),
# len(s), s[i], and re-assignment in a loop (buffer reuse).
#
# Expected UART output:
#   FSTR
#   t=23C reg=beef n=-42
#   L:20
#   B:t
#   k=0 k=1 k=2
#   pad=[  7]=[007]
from pymcu.types import uint8, uint16, int16
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


def main():
    uart = UART(115200)
    uart.println("FSTR")

    t: uint8 = 23
    reg: uint16 = 0xBEEF
    neg: int16 = -42
    s = f"t={t}C reg={reg:04x} n={neg}"
    print(s)

    n: uint16 = len(s)
    print(f"L:{n}")

    first: uint8 = s[0]
    uart.write_str("B:")
    uart.write(first)
    uart.write(10)

    k: uint8 = 0
    line = f"k={k} "
    while k < 3:
        line = f"k={k} "
        uart.write_str(line)
        k = k + 1
    uart.write(10)

    v: uint8 = 7
    p = f"pad=[{v:3d}]=[{v:03d}]"
    print(p)

    while True:
        delay_ms(1000)
