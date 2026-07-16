# generators: `yield` lowered to the coroutine state machine (zero-cost class
# with poll()/_value); `for x in gen(...)` desugars to an explicit poll loop.
#
# powers(4) yields 1, 2, 4, 8 (sum 15); the second loop breaks at the first
# power greater than 4 (= 8), abandoning the generator mid-iteration.
#
# Expected UART output:
#   GEN
#   1
#   2
#   4
#   8
#   S:15
#   F:8
from pymcu.types import uint16, uint32
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


def powers(n: uint32):
    p: uint32 = 1
    k: uint32 = 0
    while k < n:
        yield p
        p = p * 2
        k = k + 1


def main():
    uart = UART(115200)
    uart.println("GEN")

    total: uint32 = 0
    for v in powers(4):
        t: uint16 = v
        print(t)
        total = total + v
    print(f"S:{total}")

    found: uint32 = 0
    for w in powers(10):
        if w > 4:
            found = w
            break
    print(f"F:{found}")

    while True:
        delay_ms(1000)
