#!/usr/bin/env python3
"""
External live variable monitor for Keil firmware projects.

The first transport is J-Link Commander because it is common in Keil/MDK
workflows and can read target RAM without Keil's Watch window. Keep the script
standard-library only so it can run on an engineering PC with minimal setup.
"""

from __future__ import annotations

import argparse
import json
import os
import random
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from typing import Dict, Iterable, List, Optional, Tuple


TYPE_FORMATS = {
    "uint8": ("B", 1),
    "int8": ("b", 1),
    "uint16": ("H", 2),
    "int16": ("h", 2),
    "uint32": ("I", 4),
    "int32": ("i", 4),
    "float": ("f", 4),
    "float32": ("f", 4),
    "double": ("d", 8),
    "float64": ("d", 8),
    "bool": ("B", 1),
}


@dataclass
class WatchVar:
    name: str
    address: int
    type_name: str
    size_override: Optional[int] = None
    fmt: str = "dec"
    scale: float = 1.0
    offset: float = 0.0
    unit: str = ""

    @property
    def size(self) -> int:
        if self.type_name == "bytes":
            if not self.size_override:
                raise WatchError(f"{self.name}: bytes type requires size")
            return self.size_override
        return TYPE_FORMATS[self.type_name][1]


class WatchError(RuntimeError):
    pass


def parse_int(value: object) -> int:
    if isinstance(value, int):
        return value
    if isinstance(value, str):
        return int(value.strip(), 0)
    raise ValueError(f"invalid integer value: {value!r}")


def load_config(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def parse_keil_map_symbols(path: str) -> Dict[str, int]:
    """Best-effort symbol extraction from ARM/Keil linker map files.

    Keil map layouts vary across ARMCC/ARMCLANG versions, so this parser is
    intentionally conservative. Explicit addresses in the JSON config remain
    the most reliable method.
    """

    if not path:
        return {}
    if not os.path.exists(path):
        raise WatchError(f"map file not found: {path}")

    symbols: Dict[str, int] = {}
    arm_symbol_re = re.compile(
        r"^\s*(?P<name>[A-Za-z_.$][A-Za-z0-9_.$]*)\s+"
        r"(?P<addr>0x[0-9a-fA-F]{6,16})\s+"
        r"(?:(?P<ov>(?!Data\b|Number\b)\S+)\s+)?"
        r"(?P<type>Data|Number)\s+"
        r"(?P<size>\d+)\s+"
        r"(?P<object>.+)$"
    )
    addr_first_re = re.compile(
        r"(?P<addr>0x[0-9a-fA-F]{6,16})\s+.*?\b(?P<name>[A-Za-z_][A-Za-z0-9_$]*)\s*$"
    )
    reject = {
        "Execution",
        "Load",
        "Base",
        "Size",
        "Type",
        "Attr",
        "Section",
        "Name",
        "Object",
    }

    with open(path, "r", encoding="utf-8", errors="ignore") as f:
        for line in f:
            stripped = line.strip()
            m = arm_symbol_re.match(stripped)
            if m and m.group("type") == "Data":
                name = m.group("name")
                try:
                    addr = int(m.group("addr"), 16)
                    size = int(m.group("size"), 10)
                except ValueError:
                    continue
                if size > 0 and is_probable_ram_address(addr):
                    symbols.setdefault(name, addr)
                continue

            m = addr_first_re.search(stripped)
            if not m:
                continue
            name = m.group("name")
            if name in reject or "." in name:
                continue
            try:
                addr = int(m.group("addr"), 16)
            except ValueError:
                continue
            if is_probable_ram_address(addr):
                symbols.setdefault(name, addr)

    return symbols


def is_probable_ram_address(addr: int) -> bool:
    return (
        0x10000000 <= addr <= 0x1000FFFF
        or 0x20000000 <= addr <= 0x200FFFFF
        or 0x1FFF0000 <= addr <= 0x1FFFFFFF
    )


def build_watch_list(config: dict, symbols: Dict[str, int]) -> List[WatchVar]:
    result: List[WatchVar] = []
    for item in config.get("variables", []):
        name = item["name"]
        type_name = item.get("type", "uint32").lower()
        if type_name not in TYPE_FORMATS and type_name != "bytes":
            valid = ", ".join(sorted(TYPE_FORMATS) + ["bytes"])
            raise WatchError(f"{name}: unsupported type {type_name!r}; valid: {valid}")

        if "address" in item and str(item["address"]).strip():
            address = parse_int(item["address"])
        elif "symbol" in item and str(item["symbol"]).strip():
            symbol = str(item["symbol"]).strip()
            if symbol not in symbols:
                raise WatchError(f"{name}: base symbol {symbol!r} not found in map file")
            address = symbols[symbol] + parse_int(item.get("address_offset", 0))
        elif name in symbols:
            address = symbols[name]
        else:
            raise WatchError(f"{name}: no address and symbol not found in map file")

        result.append(
            WatchVar(
                name=name,
                address=address,
                type_name=type_name,
                size_override=int(item["size"]) if "size" in item else None,
                fmt=item.get("format", "dec").lower(),
                scale=float(item.get("scale", 1.0)),
                offset=float(item.get("offset", 0.0)),
                unit=str(item.get("unit", "")),
            )
        )
    if not result:
        raise WatchError("no variables configured")
    return result


class MemoryReader:
    def read(self, reads: Iterable[Tuple[int, int]]) -> Dict[int, bytes]:
        raise NotImplementedError


class MockMemoryReader(MemoryReader):
    def __init__(self) -> None:
        self.tick = 0

    def read(self, reads: Iterable[Tuple[int, int]]) -> Dict[int, bytes]:
        self.tick += 1
        data: Dict[int, bytes] = {}
        for address, size in reads:
            value = self.tick + (address & 0xFF) + random.randint(0, 3)
            data[address] = int(value).to_bytes(size, "little", signed=False)
        return data


class JLinkCommanderReader(MemoryReader):
    def __init__(self, config: dict) -> None:
        jlink = config.get("jlink", {})
        self.exe = jlink.get("exe", "JLink.exe")
        self.device = jlink.get("device", "")
        self.interface = jlink.get("interface", "SWD")
        self.speed_khz = int(jlink.get("speed_khz", 4000))

        exe_path = shutil.which(self.exe) or self.exe
        if not os.path.exists(exe_path) and shutil.which(self.exe) is None:
            raise WatchError(
                f"J-Link Commander not found: {self.exe}. "
                "Install SEGGER J-Link or set jlink.exe in the config."
            )
        if not self.device:
            raise WatchError("jlink.device is required")

    def read(self, reads: Iterable[Tuple[int, int]]) -> Dict[int, bytes]:
        reads = list(reads)
        commands = [
            f"device {self.device}",
            f"if {self.interface}",
            f"speed {self.speed_khz}",
            "connect",
        ]
        for address, size in reads:
            commands.append(f"mem8 0x{address:08X} {size}")
        commands.append("q")

        with tempfile.NamedTemporaryFile("w", delete=False, suffix=".jlink", encoding="ascii") as f:
            f.write("\n".join(commands))
            command_path = f.name

        try:
            proc = subprocess.run(
                [self.exe, "-CommandFile", command_path],
                capture_output=True,
                text=True,
                errors="ignore",
                timeout=10,
            )
        finally:
            try:
                os.unlink(command_path)
            except OSError:
                pass

        if proc.returncode != 0:
            raise WatchError(proc.stderr.strip() or proc.stdout.strip() or "J-Link read failed")

        return parse_jlink_mem8_output(proc.stdout, reads)


def parse_jlink_mem8_output(output: str, reads: List[Tuple[int, int]]) -> Dict[int, bytes]:
    bytes_by_addr: Dict[int, int] = {}
    line_re = re.compile(r"^\s*([0-9A-Fa-f]{8})\s*[:=]\s*(.*)$")
    byte_re = re.compile(r"\b([0-9A-Fa-f]{2})\b")

    for line in output.splitlines():
        m = line_re.match(line)
        if not m:
            continue
        base = int(m.group(1), 16)
        for offset, byte_text in enumerate(byte_re.findall(m.group(2))):
            bytes_by_addr[base + offset] = int(byte_text, 16)

    result: Dict[int, bytes] = {}
    for address, size in reads:
        chunk = []
        for offset in range(size):
            current = address + offset
            if current not in bytes_by_addr:
                raise WatchError(f"J-Link output did not include 0x{current:08X}")
            chunk.append(bytes_by_addr[current])
        result[address] = bytes(chunk)
    return result


def decode_value(var: WatchVar, raw: bytes, endian: str) -> object:
    if var.type_name == "bytes":
        return raw
    prefix = "<" if endian == "little" else ">"
    fmt, _size = TYPE_FORMATS[var.type_name]
    value = struct.unpack(prefix + fmt, raw)[0]
    if var.type_name == "bool":
        return bool(value)
    if isinstance(value, (int, float)):
        return value * var.scale + var.offset
    return value


def format_value(var: WatchVar, value: object) -> str:
    if isinstance(value, (bytes, bytearray)):
        max_bytes = 16
        text = " ".join(f"{b:02X}" for b in value[:max_bytes])
        if len(value) > max_bytes:
            text += " ..."
        return text
    if isinstance(value, bool):
        text = "ON" if value else "OFF"
    elif var.fmt == "hex" and isinstance(value, (int, float)):
        text = hex(int(value))
    elif isinstance(value, float) and not value.is_integer():
        text = f"{value:.3f}".rstrip("0").rstrip(".")
    else:
        text = str(int(value)) if isinstance(value, float) else str(value)
    return f"{text} {var.unit}".rstrip()


def clear_screen() -> None:
    os.system("cls" if os.name == "nt" else "clear")


def render_table(values: List[Tuple[WatchVar, str]], source: str) -> None:
    clear_screen()
    print("Keil Live Watch")
    print(f"source: {source}    time: {time.strftime('%H:%M:%S')}")
    print("")
    print(f"{'Variable':<28} {'Address':<12} {'Type':<8} Value")
    print("-" * 70)
    for var, text in values:
        print(f"{var.name:<28} 0x{var.address:08X} {var.type_name:<8} {text}")
    print("")
    print("Ctrl+C to stop")


def make_reader(config: dict, override_source: Optional[str]) -> Tuple[str, MemoryReader]:
    source = (override_source or config.get("source") or "jlink").lower()
    if source == "mock":
        return source, MockMemoryReader()
    if source == "jlink":
        return source, JLinkCommanderReader(config)
    raise WatchError(f"unsupported source: {source}")


def list_symbols(path: str, pattern: str) -> None:
    symbols = parse_keil_map_symbols(path)
    regex = re.compile(pattern, re.IGNORECASE)
    for name, address in sorted(symbols.items(), key=lambda item: item[1]):
        if regex.search(name):
            print(f"0x{address:08X} {name}")


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="External Keil live variable monitor")
    parser.add_argument("--config", default="watch_config.json", help="watch config JSON")
    parser.add_argument("--source", choices=["jlink", "mock"], help="override config source")
    parser.add_argument("--once", action="store_true", help="read and display once, then exit")
    parser.add_argument("--list-symbols", metavar="MAP", help="list RAM symbols from a Keil map")
    parser.add_argument("--filter", default=".", help="regex used with --list-symbols")
    args = parser.parse_args(argv)

    try:
        if args.list_symbols:
            list_symbols(args.list_symbols, args.filter)
            return 0

        config = load_config(args.config)
        map_file = config.get("map_file") or ""
        symbols = parse_keil_map_symbols(map_file) if map_file else {}
        watch_vars = build_watch_list(config, symbols)
        endian = config.get("endian", "little").lower()
        if endian not in {"little", "big"}:
            raise WatchError("endian must be little or big")

        interval_s = max(0.05, int(config.get("interval_ms", 300)) / 1000.0)
        source, reader = make_reader(config, args.source)

        while True:
            raw_data = reader.read((var.address, var.size) for var in watch_vars)
            values = []
            for var in watch_vars:
                value = decode_value(var, raw_data[var.address], endian)
                values.append((var, format_value(var, value)))
            render_table(values, source)
            if args.once:
                return 0
            time.sleep(interval_s)

    except KeyboardInterrupt:
        print("\nStopped.")
        return 0
    except (OSError, ValueError, WatchError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
