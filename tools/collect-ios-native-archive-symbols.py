#!/usr/bin/env python3
"""Collect defined external symbols from arm64 Mach-O static archives.

Apple's nm output for these universal archives can omit symbols that are still
present in archive members. This scans the arm64 object symbol tables directly;
the final IPA export-trie check remains the authority for the linked app.
"""

from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path
from typing import Iterator


ARM64 = 0x0100000C
FAT_MAGIC = 0xCAFEBABE
FAT_MAGIC_64 = 0xCAFEBABF
MACHO_64_MAGIC = 0xFEEDFACF
AR_MAGIC = b"!<arch>\n"
LC_SYMTAB = 0x2
N_STAB = 0xE0
N_TYPE = 0x0E
N_EXT = 0x01
N_ABS = 0x02
N_INDR = 0x0A
N_SECT = 0x0E


def arm64_slices(data: bytes, source: str) -> list[bytes]:
    if len(data) < 4:
        raise ValueError(f"{source}: file is truncated")
    magic = struct.unpack_from(">I", data, 0)[0]
    if magic == FAT_MAGIC:
        if len(data) < 8:
            raise ValueError(f"{source}: fat header is truncated")
        count = struct.unpack_from(">I", data, 4)[0]
        slices = []
        for index in range(count):
            offset = 8 + index * 20
            if offset + 20 > len(data):
                raise ValueError(f"{source}: fat architecture table is truncated")
            cpu_type, _cpu_subtype, slice_offset, slice_size, _align = struct.unpack_from(
                ">IIIII", data, offset
            )
            if cpu_type == ARM64:
                end = slice_offset + slice_size
                if end > len(data):
                    raise ValueError(f"{source}: arm64 slice is out of range")
                slices.append(data[slice_offset:end])
        if not slices:
            raise ValueError(f"{source}: universal file has no arm64 slice")
        return slices
    if magic == FAT_MAGIC_64:
        if len(data) < 8:
            raise ValueError(f"{source}: 64-bit fat header is truncated")
        count = struct.unpack_from(">I", data, 4)[0]
        slices = []
        for index in range(count):
            offset = 8 + index * 32
            if offset + 32 > len(data):
                raise ValueError(f"{source}: 64-bit fat architecture table is truncated")
            cpu_type, _cpu_subtype, slice_offset, slice_size, _align, _reserved = (
                struct.unpack_from(">IIQQII", data, offset)
            )
            end = slice_offset + slice_size
            if cpu_type == ARM64:
                if end > len(data):
                    raise ValueError(f"{source}: arm64 slice is out of range")
                slices.append(data[slice_offset:end])
        if not slices:
            raise ValueError(f"{source}: universal file has no arm64 slice")
        return slices
    return [data]


def ar_members(data: bytes, source: str) -> Iterator[tuple[str, bytes]]:
    if not data.startswith(AR_MAGIC):
        raise ValueError(f"{source}: arm64 slice is not a static archive")
    offset = len(AR_MAGIC)
    while offset < len(data):
        if offset + 60 > len(data):
            raise ValueError(f"{source}: archive member header is truncated")
        header = data[offset : offset + 60]
        if header[58:60] != b"`\n":
            raise ValueError(f"{source}: archive member has an invalid header trailer")
        try:
            size = int(header[48:58].decode("ascii").strip())
        except ValueError as error:
            raise ValueError(f"{source}: archive member size is invalid") from error
        data_start = offset + 60
        data_end = data_start + size
        if data_end > len(data):
            raise ValueError(f"{source}: archive member extends past end of file")
        member_data = data[data_start:data_end]
        raw_name = header[:16].decode("ascii", errors="replace").strip()
        if raw_name.startswith("#1/"):
            try:
                name_length = int(raw_name[3:])
            except ValueError as error:
                raise ValueError(f"{source}: BSD archive member name is invalid") from error
            if name_length > len(member_data):
                raise ValueError(f"{source}: BSD archive member name is truncated")
            name = member_data[:name_length].decode("utf-8", errors="replace").rstrip("\0")
            member_data = member_data[name_length:]
        else:
            name = raw_name.rstrip("/")
        yield name, member_data
        offset = data_end + (size & 1)


def macho_defined_external_symbols(data: bytes, source: str) -> set[str]:
    if len(data) < 32 or struct.unpack_from("<I", data, 0)[0] != MACHO_64_MAGIC:
        return set()
    if struct.unpack_from("<I", data, 4)[0] != ARM64:
        return set()
    command_count = struct.unpack_from("<I", data, 16)[0]
    offset = 32
    symtab = None
    for _ in range(command_count):
        if offset + 8 > len(data):
            raise ValueError(f"{source}: Mach-O load command is truncated")
        command, command_size = struct.unpack_from("<II", data, offset)
        if command_size < 8 or offset + command_size > len(data):
            raise ValueError(f"{source}: Mach-O load command has an invalid size")
        if command == LC_SYMTAB:
            if command_size < 24:
                raise ValueError(f"{source}: Mach-O symbol-table command is truncated")
            symtab = struct.unpack_from("<IIII", data, offset + 8)
        offset += command_size
    if symtab is None:
        return set()

    symbol_offset, symbol_count, string_offset, string_size = symtab
    if symbol_offset + symbol_count * 16 > len(data):
        raise ValueError(f"{source}: Mach-O symbol table is out of range")
    if string_offset + string_size > len(data):
        raise ValueError(f"{source}: Mach-O string table is out of range")
    strings = data[string_offset : string_offset + string_size]
    symbols = set()
    for index in range(symbol_count):
        entry_offset = symbol_offset + index * 16
        string_index, symbol_type, _section, _description, _value = struct.unpack_from(
            "<IBBHQ", data, entry_offset
        )
        if symbol_type & N_STAB or not symbol_type & N_EXT:
            continue
        if (symbol_type & N_TYPE) not in (N_SECT, N_ABS, N_INDR):
            continue
        if string_index == 0 or string_index >= len(strings):
            continue
        end = strings.find(b"\0", string_index)
        if end < 0:
            raise ValueError(f"{source}: Mach-O symbol name is not terminated")
        name = strings[string_index:end].decode("utf-8", errors="replace")
        # Export-list entries must be linkable ABI symbols. Apple's Mach-O C,
        # Objective-C, and C++ external names carry the leading underscore;
        # assembler-local labels such as l2327 are not exports and make ld fail.
        if name.startswith("_"):
            symbols.add(name)
    return symbols


def archive_symbols(path: Path) -> set[str]:
    try:
        data = path.read_bytes()
    except OSError as error:
        raise ValueError(f"{path}: {error}") from error
    symbols = set()
    found_arm64_object = False
    for slice_index, slice_data in enumerate(arm64_slices(data, str(path))):
        if not slice_data.startswith(AR_MAGIC):
            raise ValueError(f"{path}: arm64 slice {slice_index} is not a static archive")
        for member_name, member_data in ar_members(slice_data, str(path)):
            for member_index, object_data in enumerate(arm64_slices(member_data, f"{path}({member_name})")):
                if (
                    len(object_data) >= 32
                    and struct.unpack_from("<I", object_data, 0)[0] == MACHO_64_MAGIC
                    and struct.unpack_from("<I", object_data, 4)[0] == ARM64
                ):
                    found_arm64_object = True
                    symbols.update(
                        macho_defined_external_symbols(
                            object_data, f"{path}({member_name}, slice {member_index})"
                        )
                    )
    if not found_arm64_object:
        raise ValueError(f"{path}: archive contains no arm64 Mach-O object files")
    if not symbols:
        raise ValueError(f"{path}: archive contains no defined arm64 external symbols")
    return symbols


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archives", nargs="+", type=Path)
    args = parser.parse_args()
    try:
        all_symbols = set()
        for archive in args.archives:
            all_symbols.update(archive_symbols(archive))
    except (OSError, ValueError, struct.error) as error:
        print(f"iOS native archive scan failed: {error}", file=sys.stderr)
        return 1
    for symbol in sorted(all_symbols):
        print(symbol)
    print(
        f"Collected {len(all_symbols)} defined external arm64 symbols from {len(args.archives)} archives.",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
