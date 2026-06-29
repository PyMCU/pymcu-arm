# DMA a word from one RAM location to another, then light GP25 if it arrived.
from pymcu.hal.gpio import Pin
from pymcu.hal.dma import DMA
from pymcu.types import ptr, uint32


def main():
    led = Pin(25, Pin.OUT)
    src: ptr[uint32] = ptr(0x20010000)
    dst: ptr[uint32] = ptr(0x20010100)
    src.value = 0xDEADBEEF
    dst.value = 0

    dma = DMA(0)
    dma.transfer(0x20010000, 0x20010100, 1)

    if dst.value == 0xDEADBEEF:
        led.high()
    else:
        led.low()
    while True:
        pass
