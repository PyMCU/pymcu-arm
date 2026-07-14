from pymcu.hal.gpio import Pin
from pymcu.types import inline

class Radio:
    @inline
    def __init__(self):
        self._led = Pin(25, Pin.OUT)
    @inline
    def light(self):
        self._led.high()
