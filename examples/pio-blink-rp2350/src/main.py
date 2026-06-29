# Blink GP25 entirely from a PIO state machine (no CPU in the loop).
import rp2
from rp2 import PIO, StateMachine


@rp2.asm_pio(set_init=[PIO.OUT_LOW])
def blink():
    set(pindirs, 1)
    wrap_target()
    set(pins, 1)[20]
    set(pins, 0)[20]
    wrap()


def main():
    sm = StateMachine(0, blink, freq=1000000, set_base=25)
    while True:
        pass
