# CYW43439 WiFi over WPA2-PSK: gSPI bring-up -> the ten-ioctl join -> drain the post-join
# async events.
#
# WPA2 here is not cryptography. The four-way handshake runs inside the CYW43439's own
# firmware; the host's whole job is to hand the chip the passphrase and the auth parameters
# and then send the same WLC_SET_SSID an open join sends. That is why this file differs
# from wifi-cyw43-rp2040 by exactly one argument: the passphrase handed to connect().
from pymcu.hal.wifi import CYW43
from pymcu.types import ptr, uint32


def main():
    wifi = CYW43()
    wifi.connect("RP2350Sharp-AP", "hunter2")
    slot: ptr[uint32] = ptr(0x20000000)
    slot.value = 1
    while True:
        pass
