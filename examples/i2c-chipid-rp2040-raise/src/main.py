# Regression repro for PyMCU/PyMCU#I2C_RAW_INTR_TX_ABRT -- write_then_read's
# caller raises OSError on a nonzero abort code, exactly as busio.py's
# writeto_then_readfrom does. Before the fix (I2C_RAW_INTR_TX_ABRT reading
# bit 9 / STOP_DET instead of bit 6 / TX_ABRT), write_then_read reported an
# abort on this ENTIRELY SUCCESSFUL transaction, inner() legitimately raised,
# and the firmware halted in __pymcu_unhandled_exn before ever printing DONE.
from pymcu.hal.i2c import I2C
from pymcu.types import uint8, uint16


def inner(rc: uint8):
    if rc == 1:
        raise OSError("A")
    elif rc != 0:
        raise OSError("B")


def outer(bus: I2C, address: uint8, out_buffer, out_n: uint16, in_buffer, in_n: uint16):
    rc: uint8 = bus.write_then_read(address, out_buffer, 0, out_n, in_buffer, 0, in_n)
    inner(rc)


print("RDY")
i2c = I2C()
reg: bytearray = bytearray(1)
reg[0] = 0xD0
chip_id: bytearray = bytearray(1)
outer(i2c, 0x77, reg, 1, chip_id, 1)
print("DONE")
print(chip_id[0])

while True:
    pass
