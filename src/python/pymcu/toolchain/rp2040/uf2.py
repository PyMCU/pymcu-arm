# -----------------------------------------------------------------------------
# PyMCU RP2040/RP2350 UF2 packer
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

"""
Pack a flat XIP flash image into a UF2 file -- the format the RP2040/RP2350
BOOTSEL mass-storage device accepts by drag-and-drop, and the one picotool
loads without needing an offset.

Pure Python, no external tool: the build must not depend on picotool being
installed.  A UF2 is a stream of 512-byte blocks, each carrying 256 payload
bytes plus a 32-byte header and a trailing magic word:

    offset  field
    0       magicStart0   0x0A324655 ("UF2\\n")
    4       magicStart1   0x9E5D5157
    8       flags         0x00002000 (familyID present)
    12      targetAddr    0x10000000 + blockNo * 256
    16      payloadSize   256
    20      blockNo
    24      numBlocks
    28      familyID
    32      data          256 bytes, zero-padded on the final block
    508     magicEnd      0x0AB16F30
"""

from __future__ import annotations

import struct
from pathlib import Path

UF2_MAGIC_START0 = 0x0A324655
UF2_MAGIC_START1 = 0x9E5D5157
UF2_MAGIC_END = 0x0AB16F30
UF2_FLAG_FAMILY_ID = 0x00002000

UF2_BLOCK_SIZE = 512
UF2_PAYLOAD_SIZE = 256

# Start of the XIP window: where a flat flash image is mapped on both chips.
XIP_BASE = 0x10000000

# Family IDs as picotool/pico-sdk define them (boot/uf2.h).  RP2350 has one per
# image type; a flat picobin ARM image is RP2350_ARM_S.  Loading a UF2 with the
# wrong family is rejected by the BOOTSEL bootloader, so this must match the
# image the toolchain actually linked.
RP2040_FAMILY_ID = 0xE48BFF56
RP2350_ARM_S_FAMILY_ID = 0xE48BFF59

FAMILY_IDS = {
    "rp2040": RP2040_FAMILY_ID,
    "rp2350": RP2350_ARM_S_FAMILY_ID,
}


def family_id(chip: str) -> int:
    """Return the UF2 family ID for a chip id, defaulting to the RP2040."""
    return FAMILY_IDS.get((chip or "").lower(), RP2040_FAMILY_ID)


def bin_to_uf2(image: bytes, family: int, base_addr: int = XIP_BASE) -> bytes:
    """Pack a flat flash *image* into UF2 blocks starting at *base_addr*."""
    block_count = max(1, -(-len(image) // UF2_PAYLOAD_SIZE))  # ceil
    out = bytearray()

    for block_no in range(block_count):
        start = block_no * UF2_PAYLOAD_SIZE
        chunk = image[start:start + UF2_PAYLOAD_SIZE]
        # payloadSize stays 256 on the final short block; the tail is zero-fill.
        out += struct.pack(
            "<8I",
            UF2_MAGIC_START0,
            UF2_MAGIC_START1,
            UF2_FLAG_FAMILY_ID,
            base_addr + start,
            UF2_PAYLOAD_SIZE,
            block_no,
            block_count,
            family,
        )
        out += chunk.ljust(UF2_PAYLOAD_SIZE, b"\x00")
        out += b"\x00" * (UF2_BLOCK_SIZE - 32 - UF2_PAYLOAD_SIZE - 4)
        out += struct.pack("<I", UF2_MAGIC_END)

    return bytes(out)


def write_uf2(bin_path: Path, uf2_path: Path, chip: str) -> Path:
    """Pack *bin_path* for *chip* and write it to *uf2_path*."""
    uf2_path.write_bytes(bin_to_uf2(bin_path.read_bytes(), family_id(chip)))
    return uf2_path
