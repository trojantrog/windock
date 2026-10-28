"""Build a reviewed Taskbar profile from PE/PDB files; never inspect a process.

Requires pefile and capstone. No target DLL is loaded, no COM object is created,
and no process-memory or injection API is used. Output is suitable for review,
not proof that Explorer currently exposes a complete or stable snapshot.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import re
import struct
import sys
import urllib.request
import uuid


REVIEWED_IMAGE = "ca1e335ac1fae5ff57519035302a2f365c4620d3a230f31f1231af7331760741"
SYMBOL_SERVER = "https://msdl.microsoft.com/download/symbols"
MAX_PDB_BYTES = 64 * 1024 * 1024


def require(value, message):
    if not value:
        raise ValueError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


class Pdb:
    """Bounded reader for MSF 7, PDB identity, DBI, and S_PUB32 records."""

    def __init__(self, data):
        require(len(data) <= MAX_PDB_BYTES, "PDB exceeds size limit")
        require(data.startswith(b"Microsoft C/C++ MSF 7.00\r\n\x1aDS\0\0\0"), "Unsupported PDB")
        self.data = data
        self.page_size, _, page_count, directory_size, _, block_map = struct.unpack_from("<6I", data, 32)
        require(self.page_size in (512, 1024, 2048, 4096, 8192, 16384, 32768, 65536), "Invalid MSF page size")
        require(page_count * self.page_size == len(data), "Truncated MSF pages")
        count = math.ceil(directory_size / self.page_size)
        require(count * 4 <= self.page_size, "Unsupported multi-page MSF block map")
        table = self.pages([block_map])
        directory = self.pages(struct.unpack_from(f"<{count}I", table))[:directory_size]
        streams = struct.unpack_from("<I", directory)[0]
        require(4 <= streams <= 65536 and 4 + streams * 4 <= len(directory), "Invalid stream directory")
        sizes = struct.unpack_from(f"<{streams}I", directory, 4)
        at = 4 + streams * 4
        self.streams = []
        for size in sizes:
            require(size == 0xFFFFFFFF or size <= len(data), "Invalid PDB stream size")
            blocks = 0 if size == 0xFFFFFFFF else math.ceil(size / self.page_size)
            require(at + blocks * 4 <= len(directory), "Truncated PDB stream map")
            indices = struct.unpack_from(f"<{blocks}I", directory, at)
            self.streams.append((size, indices))
            at += blocks * 4

    def pages(self, indices):
        result = []
        for page in indices:
            start = page * self.page_size
            require(start + self.page_size <= len(self.data), "PDB page outside file")
            result.append(self.data[start:start + self.page_size])
        return b"".join(result)

    def stream(self, index):
        require(0 <= index < len(self.streams), "Missing PDB stream")
        size, pages = self.streams[index]
        return b"" if size == 0xFFFFFFFF else self.pages(pages)[:size]

    def validate(self, image):
        info, dbi = self.stream(1), self.stream(3)
        require(len(info) >= 28 and len(dbi) >= 64, "Missing PDB identity")
        guid = str(uuid.UUID(bytes_le=info[12:28]))
        info_age, dbi_age = struct.unpack_from("<I", info, 8)[0], struct.unpack_from("<I", dbi, 8)[0]
        # Public stripping can advance PDB info age without changing linked DBI
        # age. This is the rule used by Microsoft's PDB1::OpenValidate4.
        require(guid == image["pdbGuid"] and info_age >= image["pdbAge"] and dbi_age == image["pdbAge"],
                "PDB identity does not match PE")
        require(struct.unpack_from("<H", dbi, 58)[0] == image["machine"], "PDB machine mismatch")
        sizes = struct.unpack_from("<5I", dbi, 24)
        optional_size, ec_size = struct.unpack_from("<II", dbi, 48)
        at = 64 + sum(sizes) + ec_size
        require(optional_size % 2 == 0 and at + optional_size <= len(dbi), "Invalid DBI debug header")
        debug = struct.unpack_from(f"<{optional_size // 2}H", dbi, at)
        require(len(debug) > 5 and debug[3] == debug[4] == 0xFFFF, "OMAP requires a separate decoder")
        self.section_headers = self.stream(debug[5])
        return {"guid": guid, "infoAge": info_age, "dbiAge": dbi_age, "sha256": sha256(self.data), "bytes": len(self.data)}

    def symbols(self, pe):
        dbi = self.stream(3)
        stream = self.stream(struct.unpack_from("<H", dbi, 20)[0])
        require(len(self.section_headers) == 40 * len(pe.sections), "PDB section count mismatch")
        for index, section in enumerate(pe.sections):
            require(self.section_headers[index * 40:(index + 1) * 40] == section.__pack__(), "PDB section layout mismatch")
        # This helper only decodes symbol strings. It does not load the target
        # DLL/PDB through DbgHelp and does not enumerate a process.
        dbghelp = ctypes.WinDLL(str(Path(os.environ["SystemRoot"]) / "System32" / "dbghelp.dll"))
        undecorate = dbghelp.UnDecorateSymbolName
        undecorate.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint32, ctypes.c_uint32]
        undecorate.restype = ctypes.c_uint32
        result = []
        at = 0
        while at + 4 <= len(stream):
            length, kind = struct.unpack_from("<HH", stream, at)
            end = at + length + 2
            require(length >= 2 and end <= len(stream), "Invalid symbol record")
            if kind == 0x110E and length >= 12:
                _, offset, segment = struct.unpack_from("<IIH", stream, at + 4)
                raw = stream[at + 14:end].split(b"\0", 1)[0]
                if 0 < segment <= len(pe.sections):
                    section = pe.sections[segment - 1]
                    require(offset < max(section.Misc_VirtualSize, section.SizeOfRawData), "Symbol outside PE section")
                    buffer = ctypes.create_string_buffer(16384)
                    count = undecorate(raw, buffer, len(buffer), 0)
                    name = (buffer.value if count else raw).decode("utf-8", "replace")
                    result.append({"rva": section.VirtualAddress + offset, "name": name, "decorated": raw.decode("ascii", "replace")})
            at = end
        return result


def image_identity(pe, data):
    require(pe.FILE_HEADER.Machine == 0x8664 and pe.OPTIONAL_HEADER.Magic == 0x20B, "Only reviewed AMD64 PE is supported")
    records = []
    for debug in getattr(pe, "DIRECTORY_ENTRY_DEBUG", []):
        if debug.struct.Type != 2:
            continue
        value = pe.get_data(debug.struct.AddressOfRawData, debug.struct.SizeOfData)
        if value[:4] == b"RSDS" and len(value) >= 25:
            records.append((str(uuid.UUID(bytes_le=value[4:20])), struct.unpack_from("<I", value, 20)[0],
                            value[24:].split(b"\0", 1)[0].decode("utf-8").replace("\\", "/").split("/")[-1]))
    require(len(records) == 1, "Expected exactly one RSDS identity")
    guid, age, name = records[0]
    require(name == "Taskbar.pdb", "Unexpected PDB name")
    return {"fileName": "Taskbar.dll", "machine": pe.FILE_HEADER.Machine,
            "timeDateStamp": pe.FILE_HEADER.TimeDateStamp, "sizeOfImage": pe.OPTIONAL_HEADER.SizeOfImage,
            "fileLength": len(data), "sha256": sha256(data), "pdbName": name, "pdbGuid": guid, "pdbAge": age}


class Evidence:
    def __init__(self, pe, public, decoder):
        self.pe, self.public, self.decoder = pe, public, decoder
        self.profiles, self.instructions, self.notes = {}, {}, []
        self.starts = sorted({item["rva"] for item in public})
        self.functions = {entry.struct.BeginAddress: entry.struct.EndAddress for entry in pe.DIRECTORY_ENTRY_EXCEPTION}
        self.relocations = [item.rva for block in getattr(pe, "DIRECTORY_ENTRY_BASERELOC", []) for item in block.entries if item.type]

    def select(self, key, predicate, data=False, data_size=8):
        matches = [item for item in self.public if predicate(item["name"])]
        addresses = {item["rva"] for item in matches}
        require(len(addresses) == 1, f"{key}: missing or ambiguous symbol")
        start = addresses.pop()
        section = self.pe.get_section_by_rva(start)
        require(section is not None, f"{key}: missing PE section")
        executable = bool(section.Characteristics & 0x20000000)
        require(executable != data, f"{key}: incorrect section permissions")
        if data:
            require(0 < data_size <= 4096 and data_size % 8 == 0, f"{key}: invalid data size")
            end = start + data_size
        elif start in self.functions:
            end = self.functions[start]
        else:
            # Leaf functions have no unwind record. Limit to their next symbol;
            # require a terminating RET and only INT3 padding after it.
            end = min(address for address in self.starts if address > start)
            require(end - start <= 256, f"{key}: unbounded leaf function")
        require(0 < end - start <= 16384, f"{key}: function size outside limit")
        raw = self.pe.get_data(start, end - start)
        zero_filled = False
        if data and not raw and start >= section.VirtualAddress + section.SizeOfRawData:
            require(end <= section.VirtualAddress + section.Misc_VirtualSize, f"{key}: outside virtual data")
            raw = bytes(end - start)
            zero_filled = True
        require(len(raw) == end - start, f"{key}: truncated code")
        if not data:
            require(not any(start <= rva < end for rva in self.relocations), f"{key}: relocated code needs a different comparison")
            instructions = list(self.decoder.disasm(raw, start))
            require(sum(item.size for item in instructions) == len(raw), f"{key}: incomplete disassembly")
            if start not in self.functions:
                last = len(instructions) - 1
                while last >= 0 and instructions[last].mnemonic == "int3":
                    last -= 1
                require(last >= 0 and instructions[last].mnemonic == "ret", f"{key}: unsupported leaf boundary")
            self.instructions[key] = instructions
        self.profiles[key] = {"kind": "data" if data else "function", "rva": start, "size": len(raw),
                              "bytes": raw.hex().upper(), "sha256": sha256(raw), "name": matches[0]["name"]}
        if zero_filled:
            self.profiles[key]["zeroFilled"] = True
        return start

    def text(self, key):
        return "\n".join(f"{item.mnemonic} {item.op_str}".rstrip() for item in self.instructions[key])

    def capture(self, key, pattern, group=1):
        matches = list(re.finditer(pattern, self.text(key)))
        require(len(matches) == 1, f"{key}: evidence pattern missing/ambiguous: {pattern}")
        return int(matches[0].group(group), 16)

    def verify(self, key, pattern):
        require(re.search(pattern, self.text(key)) is not None, f"{key}: missing independent evidence: {pattern}")

    def vtable(self, key, predicate, methods):
        """Bind the entire IInspectable table to its public ABI method symbols."""
        start = self.select(key, predicate, data=True, data_size=(6 + len(methods)) * 8)
        profile = self.profiles[key]
        require(min(value for value in self.starts if value > start) == start + profile["size"],
                f"{key}: vtable boundary is not independently bounded by PDB")
        raw = bytes.fromhex(profile["bytes"])
        targets = []
        for slot in range(len(raw) // 8):
            target = struct.unpack_from("<Q", raw, slot * 8)[0] - self.pe.OPTIONAL_HEADER.ImageBase
            section = self.pe.get_section_by_rva(target)
            require(section is not None and section.Characteristics & 0x20000000,
                    f"{key}: vtable slot is not in the image's executable section")
            require(start + slot * 8 in self.relocations, f"{key}: pointer is missing its PE relocation")
            if slot >= 6:
                require(any(item["rva"] == target and methods[slot - 6] in item["name"] for item in self.public),
                        f"{key}: ABI slot does not match its method symbol")
            targets.append(target)
        return targets

    def verify_vtable_write(self, key, table):
        matches = [item for item in self.instructions[key]
                   if item.mnemonic == "lea" and re.fullmatch(r"rdx, \[rip \+ 0x[0-9a-f]+\]", item.op_str)]
        require(len(matches) == 1, f"{key}: ambiguous vtable initialization")
        instruction = matches[0]
        displacement = int(re.search(r"0x[0-9a-f]+", instruction.op_str).group(), 16)
        require(instruction.address + instruction.size + displacement == self.profiles[table]["rva"],
                f"{key}: constructor writes a different vtable")
        self.verify(key, r"lea rdx, \[rip \+ 0x[0-9a-f]+\]\nmov rax, rcx\nmov qword ptr \[rcx\], rdx")

    def disassembly(self):
        blocks = []
        for key, instructions in self.instructions.items():
            value = self.profiles[key]
            blocks.append(f"\n{key} RVA=0x{value['rva']:X} size={value['size']} SHA256={value['sha256']}\n{value['name']}")
            blocks.extend(f"{item.address:08X} {item.bytes.hex().upper():32s} {item.mnemonic:8s} {item.op_str}" for item in instructions)
        return "\n".join(blocks) + "\n"


def build_metadata(e, layout):
    """Optional placement/text evidence; no NIS_HIDDEN or pixel-data inference."""
    roles = {
        "managerConstructor": "NotificationAreaIconManager2::NotificationAreaIconManager2(",
        "promoted": "NotificationAreaIconManager2::VisiblePromotedIcons(",
        "overflow": "NotificationAreaIconManager2::VisibleOverflowIcons(",
        "isVisible": "NotificationAreaIconManager2::IsIconCurrentlyVisible(",
        "addVisible": "NotificationAreaIconManager2::AddIconToVisibleCollection(",
        "removeVisible": "NotificationAreaIconManager2::RemoveIconFromVisibleCollection(",
        "setTooltip": "NotificationAreaIcon2::SetTooltip(",
        "stringConstructor": "winrt::hstring::hstring(unsigned short const * __ptr64,unsigned int)",
        "precreateString": "winrt::impl::precreate_hstring_on_heap(",
        "createString": "winrt::impl::create_hstring_on_heap(",
        "attachString": "winrt::handle_type<struct winrt::impl::hstring_traits>::attach(",
        "stringBuffer": "winrt::hstring::c_str(",
        "appOwnsToolTips": "NotificationAreaIcon2::AppOwnsToolTips(",
    }
    for key, marker in roles.items():
        e.select(key, lambda name, marker=marker: marker in name)
    e.select("observableFactory", lambda n: "winrt::single_threaded_observable_vector<" in n and "NotificationAreaIcon" in n)
    e.select("vectorConstructor", lambda n: "convertible_observable_vector<" in n and "NotificationAreaIcon" in n
             and ">::convertible_observable_vector<" in n)
    for key, interface in (("observable", "IObservableVector"), ("vector", "IVector")):
        for suffix, typename in (("Producer", "producer_convert"), ("ProducerBase", "producer"), ("Produce", "produce")):
            marker = f"{interface}<struct winrt::WindowsUdk::UI::Shell::NotificationAreaIcon>"
            separator = ",void>" if typename != "produce" else " >"
            signature = marker + separator + f"::{typename}<"
            e.select(key + suffix, lambda n, typename=typename, signature=signature:
                     f"winrt::impl::{typename}<" in n and "NotificationAreaIcon" in n and signature in n)
    for key, member in (("typedVectorSize", "get_Size"), ("typedVectorAt", "GetAt")):
        e.select(key, lambda n, member=member: "NotificationAreaIcon" in n
                 and f"IVector<struct winrt::WindowsUdk::UI::Shell::NotificationAreaIcon> >::{member}(" in n)
    e.select("vectorBaseAt", lambda n: "winrt::vector_view_base<" in n and "NotificationAreaIcon" in n and ">::GetAt(" in n)
    consume = ("consume_Windows_Foundation_Collections_IVector<struct winrt::Windows::Foundation::Collections::"
               "IObservableVector<struct winrt::WindowsUdk::UI::Shell::NotificationAreaIcon>")
    for key, member in (("consumeVectorSize", "Size"), ("consumeVectorAt", "GetAt"), ("consumeVectorInsert", "InsertAt")):
        e.select(key, lambda n, member=member: consume in n and f"::{member}(" in n)
    e.select("findVisibleIdentity", lambda n: " __cdecl std::find<struct winrt::impl::fast_iterator<" in n
             and "NotificationAreaIcon" in n and "NotificationAreaIconIdentity" in n)
    e.select("getTip", lambda n: "NotificationAreaIcon2," in n and "::get_ToolTipText(" in n)
    e.select("getAppOwnsToolTips", lambda n: "NotificationAreaIcon2," in n and "::get_AppOwnsToolTips(" in n)

    collection = "const winrt::impl::produce<struct winrt::impl::convertible_observable_vector<"
    for key, interface, methods in (
        ("observableVtable", "IObservableVector", ["::add_VectorChanged(", "::remove_VectorChanged("]),
        ("vectorVtable", "IVector", ["::GetAt(", "::get_Size(", "::GetView(", "::IndexOf(", "::SetAt(", "::InsertAt(",
                                      "::RemoveAt(", "::Append(", "::RemoveAtEnd(", "::Clear(", "::GetMany(", "::ReplaceAll("]),
    ):
        marker = f"Collections::{interface}<struct winrt::WindowsUdk::UI::Shell::NotificationAreaIcon> >::"
        targets = e.vtable(key, lambda n, marker=marker: n.startswith(collection) and marker in n and "vftable" in n, methods)
        if key == "vectorVtable":
            require(targets[6] == e.profiles["typedVectorAt"]["rva"] and targets[7] == e.profiles["typedVectorSize"]["rva"],
                    "Typed vector slots disagree with verified getter functions")
    item_targets = e.vtable("itemInterfaceVtable", lambda n:
        n.startswith("const winrt::impl::produce<struct winrt::WindowsUdk::UI::Shell::implementation::NotificationAreaIcon2,")
        and "INotificationAreaIcon>::" in n and "vftable" in n,
        ["::get_ToolTipText(", "::get_AppOwnsToolTips(", "::get_Icon(", "::get_Bounds(", "::put_Bounds(",
         "::ToolTipRequested(", "::MouseInteracted(", "::Clicked(", "::ContextRequested("])
    require(item_targets[6] == e.profiles["getTip"]["rva"], "Item interface tooltip getter disagrees")
    require(item_targets[7] == e.profiles["getAppOwnsToolTips"]["rva"], "Item interface tooltip-mode getter disagrees")
    e.verify_vtable_write("observableProduce", "observableVtable")
    e.verify_vtable_write("vectorProduce", "vectorVtable")

    field = r"(0x[0-9a-f]+|[0-9]+)"
    promoted = e.capture("promoted", rf"mov rax, qword ptr \[rcx \+ {field}\]")
    overflow = e.capture("overflow", rf"mov rax, qword ptr \[rcx \+ {field}\]")
    observable_offset = e.capture("observableFactory", rf"lea rcx, \[r8 \+ {field}\]\nneg r8")
    vector_offset = e.capture("typedVectorSize", rf"lea rax, \[rcx - {field}\]")
    begin = e.capture("vectorConstructor", rf"mov qword ptr \[r10 \+ {field}\], rax\nmov rax, r10")
    end = e.capture("vectorConstructor", rf"mov qword ptr \[r10 \+ {field}\], rcx")
    capacity = e.capture("vectorConstructor", rf"mov qword ptr \[r10 \+ {field}\], rdx")
    item_offset = e.capture("addVisible", rf"lea rax, \[rsi \+ {field}\]\nmov qword ptr \[rsp \+ 0x70\], rax")
    e.verify("managerConstructor", rf"add rcx, {promoted:#x}\nlea rdx, \[rbp - 0x18\]\ncall {e.profiles['observableFactory']['rva']:#x}")
    e.verify("managerConstructor", rf"lea rcx, \[rsi \+ {overflow:#x}\]\nlea rdx, \[rbp - 0x18\]\ncall {e.profiles['observableFactory']['rva']:#x}")
    e.verify("observableFactory", rf"mov rcx, rax\ncall {e.profiles['vectorConstructor']['rva']:#x}")
    for key, offset in (("observable", observable_offset), ("vector", vector_offset)):
        setup = rf"add rcx, {offset:#x}" if key == "observable" else rf"lea rcx, \[r10 \+ {offset:#x}\]"
        e.verify("vectorConstructor", setup + rf"\ncall {e.profiles[key + 'Producer']['rva']:#x}")
        e.verify(key + "Producer", rf"call {e.profiles[key + 'ProducerBase']['rva']:#x}")
        e.verify(key + "ProducerBase", rf"call {e.profiles[key + 'Produce']['rva']:#x}")
    e.verify("typedVectorSize", rf"mov rdx, qword ptr \[rcx \+ {end:#x}\]")
    e.verify("typedVectorSize", rf"sub rdx, qword ptr \[rax \+ {begin:#x}\]\nsar rdx, 3")
    e.verify("typedVectorAt", rf"lea rax, \[rdx - {vector_offset:#x}\]")
    e.verify("typedVectorAt", rf"call {e.profiles['vectorBaseAt']['rva']:#x}")
    e.verify("vectorBaseAt", r"mov rdx, qword ptr \[rcx \+ rdx\*8\]")
    e.verify("addVisible", rf"call {e.profiles['consumeVectorInsert']['rva']:#x}")
    for key, register in (("isVisible", "r13"), ("removeVisible", "rsi")):
        e.verify(key, rf"lea rdi, \[{register} \+ {promoted:#x}\]")
        e.verify(key, rf"lea (rdi|rbx), \[{register} \+ {overflow:#x}\]")
        e.verify(key, rf"call {e.profiles['findVisibleIdentity']['rva']:#x}")
        e.verify(key, rf"call {e.profiles['consumeVectorSize']['rva']:#x}")
    e.verify("findVisibleIdentity", rf"call {e.profiles['consumeVectorAt']['rva']:#x}")
    require(end == begin + 8 and capacity == end + 8 and observable_offset < vector_offset < begin,
            "Collection interface and storage evidence disagree")

    tip = e.capture("setTooltip", rf"lea rcx, \[r14 \+ {field}\]\nlea rax, \[rsp \+ 0x20\]")
    getter_tip = e.capture("getTip", rf"lea rcx, \[rdi \+ {field}\]")
    header_length = e.capture("precreateString", rf"mov dword ptr \[rax \+ {field}\], ebx")
    header_buffer = e.capture("precreateString", rf"mov qword ptr \[rcx \+ {field}\], rax")
    e.verify("setTooltip", rf"call {e.profiles['stringConstructor']['rva']:#x}")
    e.verify("setTooltip", rf"call {e.profiles['attachString']['rva']:#x}")
    e.verify("stringConstructor", rf"call {e.profiles['precreateString']['rva']:#x}")
    e.verify("attachString", r"mov qword ptr \[rbx\], rdi")
    e.verify("getTip", rf"call {e.profiles['stringBuffer']['rva']:#x}")
    e.verify("getTip", rf"call {e.profiles['createString']['rva']:#x}")
    e.verify("createString", rf"call {e.profiles['precreateString']['rva']:#x}")
    e.verify("stringBuffer", rf"mov rax, qword ptr \[rax \+ {header_buffer:#x}\]")
    e.verify("precreateString", r"mov word ptr \[rcx \+ rbx\*2 \+ 0x1c\], dx")
    require(getter_tip + item_offset == tip, "Tooltip implementation/interface fields disagree")

    mode_field = e.capture("appOwnsToolTips", rf"cmp byte ptr \[rbx \+ {field}\], al")
    mode_version = e.capture("appOwnsToolTips", rf"cmp dword ptr \[rbx \+ {layout['version']:#x}\], ([0-9]+)\njae")
    true_target = e.capture("appOwnsToolTips", rf"cmp dword ptr \[rbx \+ {layout['version']:#x}\], {mode_version}\njae {field}")
    e.verify("appOwnsToolTips", rf"xor al, al\ncmp byte ptr \[rbx \+ {mode_field:#x}\], al\nje 0x[0-9a-f]+")
    require(any(item.address == true_target and item.mnemonic == "mov" and item.op_str == "al, 1"
                for item in e.instructions["appOwnsToolTips"]), "Tooltip mode version branch does not return true")
    e.verify("getAppOwnsToolTips", rf"lea rax, \[rcx - {item_offset:#x}\]")
    e.verify("getAppOwnsToolTips", rf"call {e.profiles['appOwnsToolTips']['rva']:#x}\nmov byte ptr \[rbx\], al")
    # Modify writes this bit on every accepted update, including NIF_ICON-only
    # updates. It is the inverse of NIF_SHOWTIP, not a separately retained flag.
    e.verify("modify", r"mov r15d, dword ptr \[rsi \+ 0x14\]\nmov r14b, 1")
    e.verify("modify", rf"mov eax, r15d\nshr eax, 7\nnot al\nand al, r14b\nmov byte ptr \[rbx \+ {mode_field:#x}\], al")
    placement = {"managerPromoted": promoted, "managerOverflow": overflow, "observableInterfaceOffset": observable_offset,
                 "vectorInterfaceOffset": vector_offset, "vectorBegin": begin, "vectorEnd": end,
                 "vectorCapacity": capacity, "itemInterfaceOffset": item_offset}
    text = {"field": tip, "headerLength": header_length, "headerBuffer": header_buffer}
    tooltip_mode = {"field": mode_field, "minimumVersion": mode_version}
    require(all(0 < value <= 4096 for value in (*placement.values(), *text.values(), *tooltip_mode.values())),
            "Metadata field outside reviewed bounds")
    placement["requiredFunctions"] = ["managerConstructor", "promoted", "overflow", "observableFactory", "vectorConstructor",
        "observableProducer", "observableProducerBase", "observableProduce", "vectorProducer", "vectorProducerBase", "vectorProduce",
        "typedVectorSize", "typedVectorAt", "vectorBaseAt", "consumeVectorSize", "consumeVectorAt", "consumeVectorInsert",
        "findVisibleIdentity", "addVisible", "removeVisible", "isVisible"]
    text["requiredFunctions"] = ["setTooltip", "getTip", "stringConstructor", "precreateString", "createString", "attachString", "stringBuffer"]
    tooltip_mode["requiredFunctions"] = ["appOwnsToolTips", "getAppOwnsToolTips", "modify"]
    return {"placement": placement, "tip": text, "tooltipMode": tooltip_mode}


def build_profile(pe, public, decoder, image, pdb_info):
    e = Evidence(pe, public, decoder)
    e.select("root", lambda n: n.endswith(" g_trayUI"), data=True)
    e.select("itemVtable", lambda n: n == "const winrt::WindowsUdk::UI::Shell::implementation::NotificationAreaIcon2::`vftable'", data=True)
    roles = {"copyData": "TrayUI::HandleCopyData(", "notify": "NotificationAreaIconManager2::ShellNotifyIcon(",
             "getRect": "NotificationAreaIconManager2::ShellNotifyIconGetRect(", "identity": "NotificationAreaIcon2::Identity(",
             "sendOwner": "NotificationAreaIcon2::SendMessageToOwnerWindow(", "modify": "NotificationAreaIconManager2::ModifyIcon(",
             "setVersion": "NotificationAreaIconManager2::SetVersion(", "addIcon": "NotificationAreaIconManager2::AddIcon("}
    for key, marker in roles.items():
        e.select(key, lambda n, marker=marker: marker in n)
    e.select("managerCopy", lambda n: "std::shared_ptr<class NotificationAreaIconManager2>::shared_ptr<class NotificationAreaIconManager2>(" in n)
    e.select("findIdentity", lambda n: " __cdecl std::find<" in n and "NotificationAreaIconIdentity" in n and "implementation::NotificationAreaIcon2" in n)

    field = r"(0x[0-9a-f]+)"
    window = e.capture("identity", rf"mov rax, qword ptr \[rcx \+ {field}\]\nmov rbx, rdx\nmov qword ptr \[rdx\], rax")
    identifier = e.capture("identity", rf"mov eax, dword ptr \[rcx \+ {field}\]\nmov dword ptr \[rdx \+ 8\], eax")
    guid = e.capture("identity", rf"movups xmm0, xmmword ptr \[rcx \+ {field}\]\nmovdqu xmmword ptr \[rdx \+ 0xc\], xmm0")
    require(identifier == window + 8 and guid == identifier + 4, "Identity fields disagree")
    callback = e.capture("sendOwner", rf"mov edx, dword ptr \[rbx \+ {field}\]\ntest edx, edx")
    version = e.capture("sendOwner", rf"cmp dword ptr \[rbx \+ {field}\], 4")
    e.verify("sendOwner", r"mov rbx, rcx\ncall ")
    e.verify("sendOwner", rf"mov rcx, qword ptr \[rbx \+ {window:#x}\]\ncall qword ptr \[rip \+")
    e.verify("sendOwner", rf"mov r8d, dword ptr \[rbx \+ {identifier:#x}\]\nmov r9, rsi")
    e.verify("modify", rf"mov dword ptr \[rbx \+ {callback:#x}\], edi")
    e.verify("setVersion", rf"mov dword ptr \[rbx \+ {version:#x}\], edi")
    for width, register, offset in (("qword", "rax", window), ("dword", "eax", identifier)):
        e.verify("findIdentity", rf"mov {register}, {width} ptr \[rcx \+ {offset:#x}\]")
    e.verify("findIdentity", rf"movups xmm0, xmmword ptr \[rcx \+ {guid:#x}\]")
    e.verify("findIdentity", r"add rdi, 8\ncmp rdi, rbx")

    # Both the notify and get-rectangle branches obtain the same shared manager.
    routes = []
    copy = e.text("copyData")
    for key in ("notify", "getRect"):
        route = (rf"mov rdx, qword ptr \[rdi \+ {field}\]\ntest rdx, rdx\n(?:je|jne) [^\n]+\n"
                 rf"add rdx, {field}\nlea rcx, \[rbp - 0x20\]\ncall {e.profiles['managerCopy']['rva']:#x}\nnop\n"
                 rf"mov rdx, r8\nmov rcx, qword ptr \[rbp - 0x20\]\ncall {e.profiles[key]['rva']:#x}")
        matches = list(re.finditer(route, copy))
        require(len(matches) == 1, f"copyData: {key} does not demonstrate the manager chain")
        routes.append(tuple(int(value, 16) for value in matches[0].groups()))
    require(routes[0] == routes[1], "Manager chains disagree")
    model, manager = routes[0]
    e.verify("managerCopy", r"mov rax, qword ptr \[rdx\]\nmov qword ptr \[rcx\], rax")
    e.verify("getRect", rf"mov r8, qword ptr \[rbx \+ 8\]\nmov rdx, qword ptr \[rbx\][\s\S]*?call {e.profiles['findIdentity']['rva']:#x}")
    e.verify("modify", rf"mov r8, qword ptr \[r12 \+ 8\]\nmov rdx, qword ptr \[r12\][\s\S]*?call {e.profiles['findIdentity']['rva']:#x}")
    e.verify("addIcon", r"mov rdx, qword ptr \[rdi \+ 8\]\ncmp rdx, qword ptr \[rdi \+ 0x10\]")
    e.verify("addIcon", r"mov qword ptr \[rdx\], rbx[\s\S]*?add qword ptr \[rdi \+ 8\], 8")
    e.verify("addIcon", rf"call {e.profiles['modify']['rva']:#x}")
    layout = {"model": model, "manager": manager, "managerVectorBegin": 0, "managerVectorEnd": 8,
              "window": window, "id": identifier, "guid": guid, "callback": callback, "version": version}
    require(all(0 <= value <= 4096 for value in layout.values()), "Field outside reviewed bounds")
    metadata = build_metadata(e, layout)
    profile = {"schema": 1, "image": image, "symbols": e.profiles, "layout": layout, "metadata": metadata,
               "capabilities": {"currentRegistration": True, "callback": True, "version": True,
                                "applicationHidden": False, "placement": True, "tip": True, "tooltipMode": True,
                                "icon": False, "nativeInvocation": False},
               "evidence": {"pdb": pdb_info, "reviewedImageOnly": True,
                            "processMemoryRead": False, "comActivated": False, "desktopTested": False,
                            "unknown": ["applicationHidden", "currentIconPixels",
                                        "runtimeSnapshotCompleteness", "nativeInvocationThreadAndLifetime"]}}
    return profile, e.disassembly()


def supporting_evidence(pe, public, decoder):
    """Keep unsupported-property and native-action evidence out of the profile."""
    evidence = Evidence(pe, public, decoder)
    for key, marker in {
        "deleteIcon": "NotificationAreaIconManager2::DeleteIcon(",
        "setIcon": "NotificationAreaIcon2::SetIcon(",
        "setTooltip": "NotificationAreaIcon2::SetTooltip(",
        "stringBuffer": "winrt::hstring::c_str(",
        "clicked": "NotificationAreaIcon2::Clicked(",
        "contextRequested": "NotificationAreaIcon2::ContextRequested(",
        "failFastUiThread": "NotifyIconUtilities::FailFastIfNotUIThread(",
        "promoted": "NotificationAreaIconManager2::VisiblePromotedIcons(",
        "overflow": "NotificationAreaIconManager2::VisibleOverflowIcons(",
    }.items():
        evidence.select(key, lambda name, marker=marker: marker in name)
    evidence.select("getIcon", lambda name: "implementation::NotificationAreaIcon2," in name
                    and "INotificationAreaIcon>::get_Icon(" in name)
    return evidence


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", type=Path, default=Path(r"C:\Windows\System32\Taskbar.dll"))
    parser.add_argument("--pdb", type=Path)
    parser.add_argument("--download", action="store_true", help="Download the exact PDB from Microsoft's symbol server")
    parser.add_argument("--decoder-path", type=Path, help="Optional existing directory containing the capstone package")
    parser.add_argument("--output-dir", type=Path, default=Path("publish/checks/tray-model"))
    args = parser.parse_args()
    if args.decoder_path:
        sys.path.insert(0, str(args.decoder_path.resolve()))
    import pefile
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64

    data = args.image.read_bytes()
    pe = pefile.PE(data=data)
    image = image_identity(pe, data)
    require(image["sha256"] == REVIEWED_IMAGE, "This image has no reviewed extraction recipe; no profile generated")
    key = uuid.UUID(image["pdbGuid"]).hex.upper() + format(image["pdbAge"], "X")
    url = f"{SYMBOL_SERVER}/{image['pdbName']}/{key}/{image['pdbName']}"
    pdb_path = args.pdb or args.output_dir / f"Taskbar-{key}.pdb"
    if not pdb_path.exists():
        require(args.download, "Matching PDB is missing; supply --pdb or --download")
        with urllib.request.urlopen(url, timeout=45) as response:
            raw = response.read(MAX_PDB_BYTES + 1)
        candidate = Pdb(raw)
        candidate.validate(image)
        pdb_path.parent.mkdir(parents=True, exist_ok=True)
        temporary = pdb_path.with_suffix(".download")
        temporary.write_bytes(raw)
        temporary.replace(pdb_path)
    pdb = Pdb(pdb_path.read_bytes())
    pdb_info = pdb.validate(image)
    pdb_info["source"] = url
    public = pdb.symbols(pe)
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    profile, disassembly = build_profile(pe, public, decoder, image, pdb_info)
    supporting = supporting_evidence(pe, public, decoder)
    require(args.image.read_bytes() == data, "System image changed during inspection")
    args.output_dir.mkdir(parents=True, exist_ok=True)
    output = args.output_dir / f"Taskbar-{key}.json"
    code_evidence = {"image": image, "symbols": profile["symbols"], "supportingSymbols": supporting.profiles}
    profile["symbols"] = {key: {name: value[name] for name in ("rva", "size", "sha256")}
                          for key, value in profile["symbols"].items()}
    output.write_text(json.dumps(profile, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (args.output_dir / "profile-code-evidence.json").write_text(json.dumps(code_evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (args.output_dir / "profile-disassembly.txt").write_text(disassembly, encoding="utf-8")
    (args.output_dir / "supporting-disassembly.txt").write_text(supporting.disassembly(), encoding="utf-8")
    print(json.dumps({"profile": str(output.resolve()), "image": image, "layout": profile["layout"],
                      "symbolKeys": list(profile["symbols"]), "capabilities": profile["capabilities"]}, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, struct.error, ImportError) as error:
        print(f"No profile generated: {error}", file=sys.stderr)
        sys.exit(1)
