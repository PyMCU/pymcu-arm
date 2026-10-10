# configure() reprograms an already-built bus's rate and mode -- the shape
# busio.SPI.configure() needs (build once, configure() per device after
# locking). Prints the actual programmed frequency before and after
# reconfiguring: SCR/CPSDVSR do not always land exactly on the rate asked for.
from pymcu.hal.rp2040.spi import SPI
from pymcu.types import uint32

spi = SPI(1000000, 0, 0)
print(spi.frequency())

spi.configure(2000000, 1, 1)
f: uint32 = spi.frequency()
print(f)
spi.transfer(0xAB)
spi.transfer(0xCD)
spi.transfer(0xEF)

while True:
    pass
