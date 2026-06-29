# Write a byte to an I2C device at address 0x3C on I2C0 (SDA=GP4, SCL=GP5).
from pymcu.hal.i2c import I2C


def main():
    bus = I2C(100000)
    bus.write_to(0x3C, 0x12)
    bus.write_to(0x3C, 0x34)
    while True:
        pass
