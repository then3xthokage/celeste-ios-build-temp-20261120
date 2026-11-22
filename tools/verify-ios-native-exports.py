#!/usr/bin/env python3
"""Verify that the final iOS Mach-O exports the decoder imports used by mods."""

import argparse
import plistlib
import struct
import sys
import zipfile


ARM64 = 0x0100000C
REQUIRED_SYMBOLS = (
    "_tf_gettheorasize",
    "_tf_granpos_dec",
    "_tf_readaudio2",
    "_tf_readvideo2",
)


def read_macho(path):
    if zipfile.is_zipfile(path):
        with zipfile.ZipFile(path) as archive:
            names = archive.namelist()
            plist_name = next(
                name for name in names
                if name.startswith("Payload/") and name.endswith(".app/Info.plist")
            )
            bundle_root = plist_name[:-len("Info.plist")]
            info = plistlib.loads(archive.read(plist_name))
            return archive.read(bundle_root + info["CFBundleExecutable"])
    with open(path, "rb") as stream:
        return stream.read()


def read_uleb(data, offset):
    value = 0
    shift = 0
    while True:
        if offset >= len(data) or shift > 63:
            raise ValueError("invalid ULEB128 in Mach-O export trie")
        byte = data[offset]
        offset += 1
        value |= (byte & 0x7F) << shift
        if byte & 0x80 == 0:
            return value, offset
        shift += 7


def parse_export_trie(data):
    exports = set()
    visited = set()

    def visit(offset, prefix, ancestors):
        if offset in ancestors:
            raise ValueError("export trie contains a cycle")
        key = (offset, prefix)
        if key in visited:
            return
        if offset < 0 or offset >= len(data):
            raise ValueError("export-trie child offset is out of range")
        visited.add(key)
        terminal_size, cursor = read_uleb(data, offset)
        terminal_end = cursor + terminal_size
        if terminal_end > len(data):
            raise ValueError("export-trie terminal is truncated")
        if terminal_size:
            exports.add(prefix)
        cursor = terminal_end
        if cursor >= len(data):
            raise ValueError("export-trie node is truncated")
        child_count = data[cursor]
        cursor += 1
        for _ in range(child_count):
            edge_end = data.find(b"\0", cursor)
            if edge_end < 0:
                raise ValueError("export-trie edge is unterminated")
            edge = data[cursor:edge_end].decode("utf-8")
            child, cursor = read_uleb(data, edge_end + 1)
            visit(child, prefix + edge, ancestors | {offset})

    visit(0, "", set())
    return exports


def exported_symbols(macho):
    if len(macho) < 32:
        raise ValueError("Mach-O header is truncated")
    magic, = struct.unpack_from("<I", macho)
    if magic != 0xFEEDFACF:
        raise ValueError("expected a little-endian 64-bit arm64 Mach-O executable")
    cpu_type, = struct.unpack_from("<i", macho, 4)
    if cpu_type & 0xFFFFFFFF != ARM64:
        raise ValueError("expected arm64 Mach-O, found CPU type 0x%08x" % (cpu_type & 0xFFFFFFFF))

    command_count, = struct.unpack_from("<I", macho, 16)
    cursor = 32
    trie_location = None
    for _ in range(command_count):
        if cursor + 8 > len(macho):
            raise ValueError("Mach-O load command is truncated")
        command, command_size = struct.unpack_from("<II", macho, cursor)
        if command_size < 8 or cursor + command_size > len(macho):
            raise ValueError("Mach-O load command has an invalid size")
        if command == 0x80000022:  # LC_DYLD_INFO_ONLY
            export_offset, export_size = struct.unpack_from("<II", macho, cursor + 8 + 32)
            trie_location = (export_offset, export_size)
        elif command == 0x80000033:  # LC_DYLD_EXPORTS_TRIE
            trie_location = struct.unpack_from("<II", macho, cursor + 8)
        cursor += command_size

    if not trie_location:
        raise ValueError("Mach-O has no dyld export trie")
    offset, size = trie_location
    if size == 0 or offset + size > len(macho):
        raise ValueError("Mach-O export trie is empty or out of range")
    return parse_export_trie(macho[offset:offset + size])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary_or_ipa", help="arm64 app executable or IPA archive")
    arguments = parser.parse_args()
    try:
        symbols = exported_symbols(read_macho(arguments.binary_or_ipa))
    except Exception as error:
        print("iOS native export verification failed: " + str(error), file=sys.stderr)
        return 1

    missing = [symbol for symbol in REQUIRED_SYMBOLS if symbol not in symbols]
    if missing:
        print("iOS executable is missing mod-required exports: " + ", ".join(missing), file=sys.stderr)
        return 1
    print("iOS executable exports all %d VidPlayer decoder entry points." % len(REQUIRED_SYMBOLS))
    return 0


if __name__ == "__main__":
    sys.exit(main())
