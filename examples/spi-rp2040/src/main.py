# Write a few bytes out on SPI0 (GP2=SCK, GP3=MOSI, GP4=MISO).
from pymcu.hal.spi import SPI


def main():
    spi = SPI(1000000)
    spi.transfer(0xAB)
    spi.transfer(0xCD)
    spi.transfer(0xEF)
    while True:
        pass
