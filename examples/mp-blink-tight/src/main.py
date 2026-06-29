# Tight toggle loop (no delay) -- standard MicroPython API.
from machine import Pin

led = Pin(25, Pin.OUT)
while True:
    led.value(1)
    led.value(0)
