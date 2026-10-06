# MicroPython-style UART echo on the Raspberry Pi Pico.
#
# This exact file runs unmodified under MicroPython on a Pico.
# PyMCU compiles it to bare-metal Thumb firmware with zero runtime overhead.
#
# Try it under MicroPython:
#   >>> import main; main.main()
#
# Compile with PyMCU:
#   pymcu build   (produces dist/firmware.bin)
from machine import Pin, UART


def main():
    uart = UART(0, 115200)
    led = Pin(25, Pin.OUT)
    uart.write("READY\n")
    buf = bytearray(1)
    while True:
        # readinto() is the portable read: it returns the count, or None when
        # nothing arrived within the timeout (the default timeout is 0, so this
        # never blocks) -- the same on a real board and here.
        n = uart.readinto(buf)
        if n:
            led.toggle()
            uart.write(buf)
