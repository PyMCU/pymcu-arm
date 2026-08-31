# CYW43439 WiFi: gSPI bring-up -> WLAN join -> read the post-join async events ->
# connect to the emulator's MQTT broker and PUBLISH a reading (42) to "dht".
#
# ONE source, two boards. This file is byte-identical to the other wifi-cyw43 example's
# and the only difference between them is `target` in pyproject.toml, which is the claim
# the pair exists to make: with a single CYW43439 driver, a Pico W and a Pico 2 W run the
# SAME program. A copy adapted per board would only show that a program exists for each.
# The tests assert that byte-identity, so editing one and not the other is caught.
#
# The SSID names RP2350Sharp because that is what the Pico 2 W test has always offered;
# it is a string the harness chooses, and renaming it would touch a passing test to make
# a comment read better.
from pymcu.hal.wifi import CYW43
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
