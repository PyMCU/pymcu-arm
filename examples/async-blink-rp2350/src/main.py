# Async on a Pico 2 (RP2350): two coroutines blink GP14/GP15 at 4:1 via
# `await asyncio.sleep_ms`. No RTOS, no threads -- the async transform compiles
# each `async def` to a zero-cost state machine; a cooperative loop polls them.
import asyncio
from pymcu.types import ptr, uint32
from pymcu.hal.gpio import Pin


def toggle(mask: uint32):
    xor: ptr[uint32] = ptr(0xD0000028)     # RP2350 SIO GPIO_OUT_XOR
    xor.value = mask


async def blink_a():
    while True:
        toggle(1 << 14)                    # GP14
        await asyncio.sleep_ms(400)


async def blink_b():
    while True:
        toggle(1 << 15)                    # GP15, 4x faster
        await asyncio.sleep_ms(100)


def main():
    Pin(14, Pin.OUT)                       # configure the pads as outputs
    Pin(15, Pin.OUT)
    a = blink_a()
    b = blink_b()
    while True:                            # cooperative executor
        a.poll()
        b.poll()
