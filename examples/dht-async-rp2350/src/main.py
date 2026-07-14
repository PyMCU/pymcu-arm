# Hardcore DHT demo on a Pico 2 (RP2350): THREE cooperative async tasks --
#   * blink   -> heartbeat LED on GP25
#   * sample  -> read the DHT11 on GP2 every 2 s (portable MicroPython driver)
#   * report  -> print temperature/humidity over UART0 (GP0=TX) every 2 s
# Proves the dht.py driver is portable (same source as MicroPython) AND that the
# nested-ZCA dispatch + module-level init land on real M33 silicon.
import asyncio
from machine import Pin, UART
from dht import DHT11

led    = Pin(25, Pin.OUT)
uart   = UART(0, 115200)
sensor = DHT11(Pin(2, Pin.IN))


async def blink():
    while True:
        led.toggle()
        await asyncio.sleep_ms(250)


async def sample():
    while True:
        sensor.measure()
        await asyncio.sleep_ms(2000)


async def report():
    while True:
        if sensor.failed:
            uart.println("DHT FAIL")
        else:
            uart.write("T=")
            uart.print_byte(sensor.temperature())   # "<value>\n"
            uart.write("H=")
            uart.print_byte(sensor.humidity())       # "<value>\n"
        await asyncio.sleep_ms(2000)


def main():
    a = blink()
    b = sample()
    c = report()
    while True:
        a.poll()
        b.poll()
        c.poll()
