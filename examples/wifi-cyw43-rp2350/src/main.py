# CYW43439 WiFi on a Pico 2 W: gSPI bring-up -> WLAN join -> read the post-join async
# events -> connect to the emulator's MQTT broker and PUBLISH a reading (42) to "dht".
# Validated end-to-end against the RP2350Sharp CYW43439 model + built-in broker.
from pymcu.hal.rp2350.cyw43 import CYW43
from pymcu.types import ptr, uint32, uint8


def main():
    wifi = CYW43()
    wifi.init()
    wifi.join_open("RP2350Sharp-AP")
    pkt: uint8[256] = [0] * 256
    wifi._drain_rx(pkt)
    wifi.mqtt_publish(42)
    slot: ptr[uint32] = ptr(0x20000000)
    slot.value = 1
    while True:
        pass
