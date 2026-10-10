# CircuitPython-style time.monotonic() + supervisor.ticks_ms() on RP2040.
#
# Before PyMCU#cp-timebase-arm, both of these raised at compile time: the CP
# layer imported millis()/millis_init() straight from pymcu.hal.timer, which
# has no RP2040/RP2350 branch and raises "Timer not supported on this
# architecture" at its own module top level, on EVERY import, even though
# pymcu.time (the module they should have gone through) already reads the
# RP2040 hardware TIMER directly and needs no HAL counter at all.
import time
import supervisor
from pymcu.types import uint8

print("RDY")

i: uint8 = 0
while i < 3:
    print(time.monotonic())
    print(supervisor.ticks_ms())
    time.sleep(0.05)
    i = i + 1

print("DONE")

while True:
    pass
