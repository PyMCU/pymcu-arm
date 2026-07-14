# DHT -> MQTT over WiFi, MicroPython-compat style (network.WLAN + umqtt.simple).
# This exact shape runs under MicroPython on a Pico 2 W; PyMCU compiles it bare-metal.
import network
from umqtt.simple import MQTTClient
from machine import Pin
from dht import DHT11
from pymcu.types import uint32


def main():
    wlan = network.WLAN(network.STA_IF)
    wlan.active(True)
    wlan.connect("RP2350Sharp-AP")

    sensor = DHT11(Pin(2, Pin.IN))
    sensor.measure()
    t: uint32 = sensor.temperature()

    client = MQTTClient(wlan, "pm", "192.168.4.1")
    client.connect()
    client.publish(t)                 # DHT reading -> MQTT topic "dht"
    while True:
        pass
