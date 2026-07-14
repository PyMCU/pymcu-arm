# exceptions: portable T-flag error model on ARM (RP2040).
#
# Mirrors the AVR exceptions fixtures: raise propagates via the global
# error flag + code pair, except handlers discriminate on the code, and
# an uncaught raise halts through __pymcu_unhandled_exn (prints E:<Name>).
#
# Expected UART output:
#   EXNS
#   A:caught
#   B:ok
#   C:type
#   D:local
#   E:fin
#   E:KeyError
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.exceptions import ValueError, TypeError, KeyError


def risky(x: uint8) -> uint8:
    if x == 0:
        raise ValueError
    return 42


def pick(x: uint8) -> uint8:
    if x == 1:
        raise TypeError
    return 7


def explode() -> uint8:
    raise KeyError


def main():
    uart = UART(115200)
    uart.println("EXNS")

    # A: raise in a callee, caught here
    try:
        r: uint8 = risky(0)
        uart.println("A:missed")
    except ValueError:
        uart.println("A:caught")

    # B: no raise, except not triggered
    try:
        r2: uint8 = risky(1)
        uart.println("B:ok")
    except ValueError:
        uart.println("B:caught")

    # C: handler discrimination by exception type
    try:
        r3: uint8 = pick(1)
        uart.println("C:missed")
    except ValueError:
        uart.println("C:value")
    except TypeError:
        uart.println("C:type")

    # D: raise directly inside the try body (local catch, no call boundary)
    try:
        raise ValueError
    except ValueError:
        uart.println("D:local")

    # E: finally runs after a caught raise
    try:
        r4: uint8 = risky(0)
    except ValueError:
        pass
    finally:
        uart.println("E:fin")

    # F: uncaught -> propagates to main -> runtime prints E:KeyError and halts
    r5: uint8 = explode()
    uart.println("F:missed")
