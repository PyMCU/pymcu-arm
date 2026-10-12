# examples/irremote_transmit_simpletest.py from the Adafruit bundle, as published. Upstream names
# board.IR_TX, the pin of an IR LED that only some boards carry; the Feather RP2040 has none, so
# the carrier comes out of an open pin, board.D5.
# SPDX-FileCopyrightText: 2021 ladyada for Adafruit Industries
# SPDX-License-Identifier: MIT
import time

import adafruit_irremote
import board
import pulseio

pulseout = pulseio.PulseOut(board.D5, frequency=38000, duty_cycle=2**15)
encoder = adafruit_irremote.GenericTransmit(
    header=[9500, 4500], one=[550, 550], zero=[550, 1700], trail=0
)

while True:
    encoder.transmit(pulseout, [255, 2, 255, 2])
    time.sleep(0.2)
