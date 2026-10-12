"""The pulse-capture ring: the linker scripts reserve it, the stdlib names its address.

pymcu.hal.rp.pulse (PulseIn on the RP2040 and RP2350) records into 1 KiB of RAM through a DMA
write ring, which wraps an address inside an aligned window, so the ring has to sit on a 1 KiB
boundary that nothing else uses. The linker scripts define it below the stack reserve and stop
the heap at it; the stdlib names the same address as a constant. The two live in different
repositories, so this test is where they are made to agree.
"""
import re
from pathlib import Path

import pytest

RUNTIME = Path(__file__).resolve().parents[2] / "src" / "runtime"


def size_of(text: str) -> int:
    n, unit = re.fullmatch(r"(\d+)([KM]?)", text).groups()
    return int(n) * {"": 1, "K": 1024, "M": 1024 * 1024}[unit]


def ring_of(script: str) -> tuple[int, int]:
    """(__pulse_ring, __heap_end) as the linker script computes them."""
    text = (RUNTIME / script).read_text()
    ram = re.search(r"RAM\s*\(rwx\)\s*:\s*ORIGIN\s*=\s*(0x[0-9A-Fa-f]+)\s*,\s*LENGTH\s*=\s*(\d+[KM]?)", text)
    stack_top = int(ram.group(1), 16) + size_of(ram.group(2))
    reserve = size_of(re.search(r"__PYMCU_GC_HEAP_STACK_RESERVE\s*=\s*(\d+[KM]?);", text).group(1))
    ring_expr = re.search(r"__pulse_ring\s*=\s*_stack_top\s*-\s*__PYMCU_GC_HEAP_STACK_RESERVE\s*-\s*(\d+[KM]?);", text)
    ring = stack_top - reserve - size_of(ring_expr.group(1))
    assert re.search(r"__heap_end\s*=\s*__pulse_ring;", text), f"{script}: the heap must stop at the ring"
    return ring, ring


@pytest.mark.parametrize("script,chip", [("rp2040.ld", "rp2040"), ("rp2350.ld", "rp2350")])
def test_the_ring_is_where_the_stdlib_says_and_on_a_kilobyte_boundary(script, chip):
    chips = pytest.importorskip(f"pymcu.chips.{chip}")

    ring, heap_end = ring_of(script)

    assert ring == chips.PULSE_RING_BASE
    assert ring % 1024 == 0
    assert heap_end == ring
