# async v2: await inside if/elif/else, while <cond> and for-range; break/continue
# in flattened loops; return values via _value; asyncio.gather executor.
#
# worker(4): i=0 +1, i=1 +1, i=2 +10 (if branch), i=3 +1 -> total 13.
# pinger: counts to 3 with a continue, prints P.
#
# Expected UART output:
#   AV2
#   P
#   T:13
import asyncio
from pymcu.types import uint32
from pymcu.hal.uart import UART


async def worker(n: uint32):
    total: uint32 = 0
    for i in range(n):
        if i == 2:
            await asyncio.sleep_ms(2)
            total = total + 10
        else:
            await asyncio.sleep_ms(1)
            total = total + 1
    return total


async def pinger():
    k: uint32 = 0
    while k < 3:
        await asyncio.sleep_ms(1)
        k = k + 1
        if k == 2:
            continue
    print("P")


def main():
    uart = UART(115200)
    uart.println("AV2")
    w = worker(4)
    p = pinger()
    asyncio.gather(w, p)
    r: uint32 = w._value
    print(f"T:{r}")
    while True:
        pass
