# Sensor reading -> MQTT over WiFi, CircuitPython-compat style (socketpool +
# adafruit_minimqtt). The idiomatic module-level `wifi.radio` singleton now COMPILES
# (facade re-export resolution fix), but a cross-module singleton's method still has a
# runtime self-binding gap, so the radio is constructed locally here.
from pymcu.hal.wifi import CYW43
import socketpool
import adafruit_minimqtt.adafruit_minimqtt as MQTT
from pymcu.types import uint32


def main():
    radio = CYW43()
    radio.connect("RP2350Sharp-AP")
    pool = socketpool.SocketPool(radio)
    client = MQTT.MQTT(broker="192.168.4.1", socket_pool=pool)
    client.connect()
    reading: uint32 = 42
    client.publish("dht", reading)
    while True:
        pass
