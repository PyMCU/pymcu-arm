# dict-set-literal: closed dict/set literals as compile-time lookup tables.
#
# `d = {...}` binds the literal; d[const] folds, d[runtime] lowers to a compare
# chain raising KeyError on no match; `x in {...}` tests membership; len(d) is
# a compile-time constant; string keys work with constant lookups.
#
# Expected UART output:
#   DICT
#   V:30
#   R:20
#   E:caught
#   S:1
#   S:0
#   N:3
#   M:2
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.exceptions import KeyError
from pymcu.time import delay_ms

SCALE = {0: 10, 1: 20, 2: 30}
MODES = {"low": 1, "mid": 2, "high": 3}
OK = {1, 3, 5}


def main():
    uart = UART(115200)
    uart.println("DICT")

    v: uint8 = SCALE[2]
    print(f"V:{v}")

    k: uint8 = 1
    r: uint8 = SCALE[k]
    print(f"R:{r}")

    try:
        k = 7
        bad: uint8 = SCALE[k]
        print("E:missed")
    except KeyError:
        print("E:caught")

    a: uint8 = 3
    if a in OK:
        print("S:1")
    if 4 in OK:
        print("S:bad")
    else:
        print("S:0")

    n: uint8 = len(SCALE)
    print(f"N:{n}")

    m: uint8 = MODES["mid"]
    print(f"M:{m}")

    while True:
        delay_ms(1000)
