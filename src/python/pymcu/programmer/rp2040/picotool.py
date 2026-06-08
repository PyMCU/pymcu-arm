import glob
import shutil
import subprocess
import sys
import time
from pathlib import Path

from pymcu.toolchain.sdk import HardwareProgrammer


class Rp2040Programmer(HardwareProgrammer):
    """
    Programmer for RP2040-based boards (Raspberry Pi Pico).

    Flash methods (tried in order):
    1. picotool — if found on system PATH.
    2. UF2 drag-and-drop — locate the RPI-RP2 BOOTSEL volume and copy the .uf2.

    To enter BOOTSEL mode: hold the BOOTSEL button while plugging in USB.
    The board mounts as a USB mass-storage drive named RPI-RP2.
    """

    def get_name(self) -> str:
        return "rp2040"

    def is_cached(self) -> bool:
        return True

    def install(self) -> Path:
        return Path(shutil.which("picotool") or "picotool")

    # ------------------------------------------------------------------
    # UF2 volume discovery
    # ------------------------------------------------------------------

    @staticmethod
    def find_uf2_volume() -> Path | None:
        """Return the mounted RPI-RP2 BOOTSEL volume, or None."""
        if sys.platform == "darwin":
            candidates = [Path("/Volumes/RPI-RP2")]
        elif sys.platform.startswith("linux"):
            candidates = [
                *[Path(p) for p in glob.glob("/media/*/RPI-RP2")],
                *[Path(p) for p in glob.glob("/run/media/*/RPI-RP2")],
                Path("/mnt/RPI-RP2"),
            ]
        elif sys.platform == "win32":
            candidates = _find_windows_uf2_drives()
        else:
            candidates = []

        for path in candidates:
            if path.is_dir() and (path / "INFO_UF2.TXT").exists():
                return path
        return None

    # ------------------------------------------------------------------
    # Flash
    # ------------------------------------------------------------------

    def flash(self, hex_file: Path, chip: str, *, port: str | None = None, baud: int | None = None) -> None:
        uf2_file = _resolve_uf2(hex_file)

        picotool = shutil.which("picotool")
        if picotool:
            self._flash_picotool(Path(picotool), uf2_file)
        else:
            self._flash_uf2(uf2_file)

    def _flash_picotool(self, picotool: Path, uf2_file: Path) -> None:
        self.console.print(f"[bold cyan]picotool[/bold cyan] load -f {uf2_file}")
        try:
            subprocess.run([str(picotool), "load", "-f", str(uf2_file)], check=True)
            subprocess.run([str(picotool), "reboot"], check=False)
            self.console.print("[bold green]Flash successful![/bold green]")
        except subprocess.CalledProcessError:
            raise RuntimeError(
                "picotool flash failed. Make sure the Pico is connected and in BOOTSEL mode."
            )

    def _flash_uf2(self, uf2_file: Path) -> None:
        self.console.print("[cyan]Looking for RPI-RP2 BOOTSEL volume...[/cyan]")

        volume = self.find_uf2_volume()
        if volume is None:
            # Wait up to 10 s for the user to connect the board
            self.console.print(
                "[yellow]RPI-RP2 not found. Hold BOOTSEL and plug in USB (waiting up to 10s)...[/yellow]"
            )
            for _ in range(10):
                time.sleep(1)
                volume = self.find_uf2_volume()
                if volume:
                    break

        if volume is None:
            raise RuntimeError(
                "RPI-RP2 BOOTSEL volume not found.\n"
                "Hold the BOOTSEL button while plugging in USB, then run 'pymcu flash' again.\n"
                "Alternatively, install picotool: https://github.com/raspberrypi/picotool"
            )

        dest = volume / uf2_file.name
        self.console.print(f"[bold cyan]Copying[/bold cyan] {uf2_file.name} → {volume}")
        shutil.copy2(str(uf2_file), str(dest))
        self.console.print("[bold green]Flash successful![/bold green]")


# ------------------------------------------------------------------
# Helpers
# ------------------------------------------------------------------

def _resolve_uf2(path: Path) -> Path:
    """Accept a .uf2 directly, or derive the .uf2 path from a .hex/.bin path."""
    if path.suffix == ".uf2":
        return path
    candidate = path.with_suffix(".uf2")
    if candidate.exists():
        return candidate
    raise FileNotFoundError(
        f"Expected a .uf2 file for RP2040 flashing, got: {path}\n"
        f"Looked for: {candidate}"
    )


def _find_windows_uf2_drives() -> list[Path]:
    """Scan all drive letters on Windows for an RPI-RP2 UF2 volume."""
    drives = []
    try:
        import string
        import ctypes
        bitmask = ctypes.windll.kernel32.GetLogicalDrives()
        for i, letter in enumerate(string.ascii_uppercase):
            if bitmask & (1 << i):
                drive = Path(f"{letter}:\\")
                if (drive / "INFO_UF2.TXT").exists():
                    drives.append(drive)
    except Exception:
        pass
    return drives
