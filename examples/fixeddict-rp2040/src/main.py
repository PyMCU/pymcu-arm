# fixeddict: mutable fixed-capacity dict (pymcu.types.FixedDict) -- open
# addressing over fixed arrays, no heap. Python semantics: KeyError on missing
# key, ValueError on inserting into a full dict, in/len/get/pop/clear.
#
# Expected UART output:
#   FXD
#   G:6
#   G2:7
#   C:1
#   C:0
#   L:2
#   D:99
#   P:7
#   E:caught
#   F:caught
#   Z:0
from pymcu.types import uint8, uint16
from pymcu.collections import FixedDict
from pymcu.hal.uart import UART
from pymcu.exceptions import KeyError, ValueError
from pymcu.time import delay_ms


def main():
    uart = UART(115200)
    uart.println("FXD")

    d = FixedDict(4)
    d[300] = 5
    d[42] = 7
    d[300] = 6

    g: uint16 = d[300]
    print(f"G:{g}")
    g2: uint16 = d[42]
    print(f"G2:{g2}")

    c: uint8 = 300 in d
    print(f"C:{c}")
    c = 9 in d
    print(f"C:{c}")

    n: uint8 = len(d)
    print(f"L:{n}")

    dv: uint16 = d.get(9, 99)
    print(f"D:{dv}")

    p: uint16 = d.pop(42)
    print(f"P:{p}")

    try:
        gone: uint16 = d[42]
        print("E:missed")
    except KeyError:
        print("E:caught")

    d2 = FixedDict(2)
    d2[1] = 10
    d2[2] = 20
    try:
        d2[3] = 30
        print("F:missed")
    except ValueError:
        print("F:caught")

    d.clear()
    z: uint8 = len(d)
    print(f"Z:{z}")

    while True:
        delay_ms(1000)
