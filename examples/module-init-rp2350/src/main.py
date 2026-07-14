# Module-level init: an instance built at module scope (like the MicroPython DHT's
# `sensor = DHT11(Pin(...))`) must have its construction MMIO run at startup -- and
# its nested Pin field stays usable from main. Drives GP25 so the emulator confirms
# both the module-level construction AND the nested dispatch produced real MMIO.
from machine import Pin
from utime import sleep_ms


class Blinker:
    def __init__(self, pin: Pin):
        self._pin = pin                # a machine.Pin nested in a module-level instance

    def tick(self, on: int):
        if on != 0:
            self._pin.high()           # nested dispatch from a module-level instance
        else:
            self._pin.low()


dev = Blinker(Pin(25, Pin.OUT))        # MODULE-LEVEL construction (runs at startup)


def main():
    while True:
        dev.tick(1)
        sleep_ms(200)
        dev.tick(0)
        sleep_ms(200)
