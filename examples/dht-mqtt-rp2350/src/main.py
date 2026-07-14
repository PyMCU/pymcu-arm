import asyncio
from machine import Pin
from dht import DHT11
from pymcu.hal.rp2350.cyw43 import CYW43
from pymcu.types import uint8, uint32

led  = Pin(25, Pin.OUT)
wifi = CYW43()

async def blink():
    while True:
        led.toggle()
        await asyncio.sleep_ms(250)

def main():
    wifi.init()
    wifi.join_open("RP2350Sharp-AP")
    pkt: uint8[256] = [0] * 256
    wifi._drain_rx(pkt)
    sensor = DHT11(Pin(2, Pin.IN))     # LOCAL, not module-level
    sensor.measure()
    t: uint32 = sensor.temperature()
    wifi.mqtt_publish(t)
    a = blink()
    while True:
        a.poll()
