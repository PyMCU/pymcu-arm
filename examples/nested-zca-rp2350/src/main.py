# Nested-ZCA method dispatch: a class stores a machine.Pin in a field and calls
# methods on it (pin._pin -> hal.Pin), through inheritance -- the exact shape of
# the MicroPython DHT driver. Drives GP25 so the emulator can observe that the
# nested dispatch produced real MMIO on M33 silicon.
from machine import Pin
from utime import sleep_ms


class LedBase:
    def __init__(self, pin: Pin):
        self._pin = pin           # machine.Pin stored in a (slot) field

    def turn_on(self):
        self._pin.high()          # void nested dispatch on an INHERITED field


class Led(LedBase):
    def turn_off(self):
        self._pin.low()           # void nested dispatch

    def is_on(self) -> int:
        return self._pin.value()  # value-returning nested dispatch


def main():
    led = Led(Pin(25, Pin.OUT))   # a Pin nested inside a Led, via inheritance
    last: int = 0
    while True:
        led.turn_on()
        last = led.is_on()        # exercise the value-returning nested path
        sleep_ms(300)
        led.turn_off()
        last = led.is_on()
        sleep_ms(300)
