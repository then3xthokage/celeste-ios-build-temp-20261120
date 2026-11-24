#!/usr/bin/env python3
"""Verify that the final iOS Mach-O exports native entry points used by mods."""

import argparse
import plistlib
import struct
import sys
import zipfile


ARM64 = 0x0100000C
REQUIRED_SYMBOLS = (
    "_SDL_SetHint",
    "_SDL_UIKitRunApp",
    "_SDL_ShowSimpleMessageBox",
    "_SDL_GetPlatform",
    "_tf_gettheorasize",
    "_tf_granpos_dec",
    "_tf_readaudio2",
    "_tf_readvideo2",
    "_FNA3D_GenVertexBuffer",
    "_FNA3D_SetVertexBufferData",
    "_FNA3D_AddDisposeVertexBuffer",
    "_FNA3D_GenIndexBuffer",
    "_FNA3D_SetIndexBufferData",
    "_FNA3D_AddDisposeIndexBuffer",
    "_FNA3D_ApplyVertexBufferBindings",
    "_FNA3D_DrawIndexedPrimitives",
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


def verify_managed_bundle(path):
    if not zipfile.is_zipfile(path):
        return

    with zipfile.ZipFile(path) as archive:
        names = set(archive.namelist())
        plist_name = next(
            name for name in names
            if name.startswith("Payload/") and name.endswith(".app/Info.plist")
        )
        bundle_root = plist_name[:-len("Info.plist")]
        required = (
            "Celeste.dll",
            "RuntimeAssemblies/FNA.dll",
            "RuntimeAssemblies/Everest/Celeste.Mod.mm.dll",
            "RuntimeAssemblies/Everest/MonoMod.RuntimeDetour.dll",
            "celeste/mscorlib.dll",
            "celeste/Celeste.Content.dll",
            "celeste/Celeste.dll.config",
        )
        missing = [bundle_root + name for name in required if bundle_root + name not in names]
        if missing:
            raise ValueError("iOS app bundle is missing required game/runtime files: " + ", ".join(missing))
        for name in required:
            if archive.getinfo(bundle_root + name).file_size == 0:
                raise ValueError("iOS app bundle contains an empty required game/runtime file: " + bundle_root + name)
        if not any(name.startswith(bundle_root + "Content/") and not name.endswith("/") for name in names):
            raise ValueError("iOS app bundle is missing Celeste's Content files.")


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
    parser.add_argument(
        "--native-imports",
        help="text file listing __Internal P/Invoke entry points required by bundled managed assemblies",
    )
    arguments = parser.parse_args()
    try:
        verify_managed_bundle(arguments.binary_or_ipa)
        symbols = exported_symbols(read_macho(arguments.binary_or_ipa))
        required_symbols = set(REQUIRED_SYMBOLS)
        if arguments.native_imports:
            with open(arguments.native_imports, "r", encoding="utf-8") as stream:
                required_symbols.update(
                    line.strip() for line in stream
                    if line.strip() and not line.lstrip().startswith("#")
                )
    except Exception as error:
        print("iOS native export verification failed: " + str(error), file=sys.stderr)
        return 1

    missing = sorted(symbol for symbol in required_symbols if symbol not in symbols)
    if missing:
        print("iOS executable is missing required native exports: " + ", ".join(missing), file=sys.stderr)
        return 1
    print("iOS executable exports all %d required native entry points." % len(required_symbols))
    return 0


if __name__ == "__main__":
    sys.exit(main())
