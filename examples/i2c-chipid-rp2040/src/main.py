# Reads a BMP280-shaped chip-id register (0xD0) over the RP2040 I2C HAL
# directly -- the same write-register-then-read-into transaction
# adafruit_bmp280._read_register uses (one real device's chip-id register,
# BMP280's own 0xD0 -> 0x58), through write_then_read's RESTART between the
# write and read phases.
from pymcu.hal.i2c import I2C
from pymcu.types import uint8

print("RDY")
i2c = I2C()

reg: bytearray = bytearray(1)
reg[0] = 0xD0
chip_id: bytearray = bytearray(1)
rc: uint8 = i2c.write_then_read(0x77, reg, 0, 1, chip_id, 0, 1)
print("READ_DONE")
print(chip_id[0])

while True:
    pass
