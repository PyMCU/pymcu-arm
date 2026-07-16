# slice-assign: equal-length slice assignment (element-wise copy).
#
# a[1:4] = [9, 8, 7]        -> literal source
# b[0:3] = a[2:5]           -> cross-array slice source
# a[2:5] = a[0:3]           -> SAME-array overlapping copy (snapshot semantics)
# c[:]   = b                -> whole-array source
#
# Expected UART output:
#   SLA
#   A:19871           (a = 1,9,8,7,1 -> digits)
#   B:871
#   O:19198           (a[2:5]=a[0:3] over 1,9,8,7,1 -> 1,9,1,9,8)
#   C:871
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.time import delay_ms



def main():
    uart = UART(115200)
    uart.println("SLA")

    a: uint8[5] = [1, 2, 3, 4, 1]
    a[1:4] = [9, 8, 7]
    uart.write_str("A:")
    i: uint8 = 0
    while i < 5:
        print(a[i], end="")
        i = i + 1
    print("")

    b: uint8[3] = [0, 0, 0]
    b[0:3] = a[2:5]
    uart.write_str("B:")
    i = 0
    while i < 3:
        print(b[i], end="")
        i = i + 1
    print("")

    a[2:5] = a[0:3]
    uart.write_str("O:")
    i = 0
    while i < 5:
        print(a[i], end="")
        i = i + 1
    print("")

    c: uint8[3] = [0, 0, 0]
    c[:] = b
    uart.write_str("C:")
    i = 0
    while i < 3:
        print(c[i], end="")
        i = i + 1
    print("")

    while True:
        delay_ms(1000)
