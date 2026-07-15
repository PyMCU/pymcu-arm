# infer-types: unannotated params/returns of outlined functions are inferred
# from call-site evidence (safe integer-widening join). Before this pass,
# scale(300, 2) silently truncated through the uint8 default and printed 88.
#
# Expected UART output:
#   INF
#   R:600
#   S:-15
#   Q:65540
from pymcu.types import uint16, int16, uint32
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


def scale(v, k):
    return v * k


def diff(a, b):
    return a - b


def wide(x):
    return x + 5


def main():
    uart = UART(115200)
    uart.println("INF")

    x: uint16 = 300
    r: uint16 = scale(x, 2)
    print(f"R:{r}")

    n: int16 = -20
    s: int16 = diff(n, -5)
    print(f"S:{s}")

    w: uint32 = 65535
    q: uint32 = wide(w)
    print(f"Q:{q}")

    while True:
        delay_ms(1000)
