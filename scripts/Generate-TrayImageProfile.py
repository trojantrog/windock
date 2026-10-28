"""Generate the reviewed native tray-image header from offline Microsoft PE/PDB.

Requires pefile and capstone. This script never loads Taskbar.dll, opens another
process, creates COM objects, installs hooks, or invokes a private function.
Only symbol demangling uses the system DbgHelp string API. Microsoft instruction
bytes are written solely to the explicitly selected local evidence directory;
the distributable C header contains identity, layout, and SHA-256 facts only.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import struct
import sys
import urllib.request
import uuid


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("tray_model_evidence", ROOT / "scripts/Inspect-TrayModel.py")
model = importlib.util.module_from_spec(spec)
spec.loader.exec_module(model)
require = model.require
REVIEWED_PDB = "20069f339a72cc1857f57e9cf612f8431e9742e018944ee020889eb38dead652"


class ImageEvidence(model.Evidence):
    def symbol(self, predicate):
        matches = [entry for entry in self.public if predicate(entry["name"])]
        require(len({entry["rva"] for entry in matches}) == 1, "Missing or ambiguous symbol")
        return matches[0]

    def select(self, key, predicate, data=False, data_size=8):
        try:
            return super().select(key, predicate, data, data_size)
        except ValueError as error:
            if str(error) != f"{key}: unsupported leaf boundary":
                raise
            # Adjustor thunks / refcount leaves can end in a direct tail jump.
            # They must end at the next public symbol and target named code in
            # this same image. This is not an instruction-pattern address scan.
            symbol = self.symbol(predicate)
            return self.record(key, symbol["rva"], symbol["name"])

    def record(self, key, start, name):
        from capstone.x86_const import X86_OP_IMM
        end = self.functions.get(start, min(value for value in self.starts if value > start))
        require(0 < end - start <= 16384, f"{key}: unbounded function")
        require(start in self.functions or end - start <= 256, f"{key}: unbounded leaf")
        raw = self.pe.get_data(start, end - start)
        require(len(raw) == end - start, f"{key}: truncated function")
        require(not any(start <= rva < end for rva in self.relocations), f"{key}: relocated code")
        instructions = list(self.decoder.disasm(raw, start))
        require(sum(item.size for item in instructions) == len(raw), f"{key}: incomplete disassembly")
        if start not in self.functions:
            body = [item for item in instructions if item.mnemonic != "int3"]
            require(body and body[-1].mnemonic in ("ret", "jmp"), f"{key}: unsupported leaf boundary")
            if body[-1].mnemonic == "jmp":
                operand = body[-1].operands[0]
                require(operand.type == X86_OP_IMM and operand.imm in self.starts,
                        f"{key}: tail target has no public symbol")
                section = self.pe.get_section_by_rva(operand.imm)
                require(section and section.Characteristics & 0x20000000, f"{key}: tail target is not code")
        self.instructions[key] = instructions
        self.profiles[key] = {"kind": "function", "rva": start, "size": len(raw),
                              "bytes": raw.hex().upper(), "sha256": model.sha256(raw), "name": name,
                              "boundary": "pdata" if start in self.functions else "next-public-symbol"}
        return start

    def table(self, key, predicate, expected_slots):
        start = self.select(key, predicate, data=True, data_size=8 * expected_slots)
        require(min(value for value in self.starts if value > start) == start + 8 * expected_slots,
                f"{key}: table boundary differs from the reviewed ABI")
        raw = bytes.fromhex(self.profiles[key]["bytes"])
        slots = []
        relocations = {item.rva: item.type for block in self.pe.DIRECTORY_ENTRY_BASERELOC
                       for item in block.entries if item.type}
        for index in range(expected_slots):
            target = struct.unpack_from("<Q", raw, index * 8)[0] - self.pe.OPTIONAL_HEADER.ImageBase
            section = self.pe.get_section_by_rva(target)
            require(section and section.Characteristics & 0x20000000 and target in self.starts,
                    f"{key}: unnamed or nonexecutable slot")
            require(relocations.get(start + index * 8) == 10, f"{key}: slot lacks DIR64 relocation")
            slots.append(target)
        self.profiles[key]["slots"] = slots
        return slots

    def rip_references(self, instructions, target):
        from capstone.x86_const import X86_OP_MEM, X86_REG_RIP
        return [item for item in instructions for operand in item.operands
                if operand.type == X86_OP_MEM and operand.mem.base == X86_REG_RIP
                and item.address + item.size + operand.mem.disp == target]


def build(pe, public, decoder):
    e = ImageEvidence(pe, public, decoder)
    markers = {
        "failFastUiThread": "NotifyIconUtilities::FailFastIfNotUIThread(",
        "managerConstructor": "NotificationAreaIconManager2::NotificationAreaIconManager2(",
        "makeManager": "std::make_shared<class NotificationAreaIconManager2>(",
        "taskListInitialize": "CTaskListWnd::Initialize(",
        "managerDestructor": "NotificationAreaIconManager2::~NotificationAreaIconManager2(",
        "setManager": "TaskbarModel::SetNotificationAreaIconManager2(",
        "shellNotifyIcon": "NotificationAreaIconManager2::ShellNotifyIcon(",
        "addIcon": "NotificationAreaIconManager2::AddIcon(",
        "deleteIcon": "NotificationAreaIconManager2::DeleteIcon(",
        "modifyIcon": "NotificationAreaIconManager2::ModifyIcon(",
        "setIcon": "NotificationAreaIcon2::SetIcon(",
        "hiconToSoftwareBitmap": "NotifyIconUtilities::TryGetSoftwareBitmapFromHicon(",
        "rootConstructor": "TrayUI::TrayUI(",
        "rootDestructor": "TrayUI::~TrayUI(",
        "setTaskbarModel": "TrayUI::SetTaskbarModel(",
        "wrlAtomicAddRef": "Microsoft::WRL::Details::SafeUnknownIncrementReference(",
        "wrlAtomicRelease": "Microsoft::WRL::Details::SafeUnknownDecrementReference(",
        "sharedPtrCopy": "std::shared_ptr<class NotificationAreaIconManager2>::shared_ptr<class NotificationAreaIconManager2>(",
        "sharedPtrRelease": "std::_Ref_count_base::_Decref(",
        "copyData": "TrayUI::HandleCopyData(",
        "getRect": "NotificationAreaIconManager2::ShellNotifyIconGetRect(",
        "identity": "NotificationAreaIcon2::Identity(",
        "itemWeakRelease": "winrt::impl::weak_ref<1,1>::decrement_strong(",
    }
    for key, marker in markers.items():
        e.select(key, lambda name, marker=marker: marker in name)
    for key, member in (("itemAddRefThunk", "AddRef"), ("itemRelease", "Release")):
        e.select(key, lambda n, member=member: "produce_base<struct winrt::WindowsUdk::UI::Shell::implementation::NotificationAreaIcon2," in n
                 and f"INotificationAreaIcon,void>::{member}(" in n)
    for key, member in (("itemAddRef", "AddRef"), ("itemSubtractReference", "subtract_final_reference")):
        e.select(key, lambda n, member=member: "root_implements<struct winrt::WindowsUdk::UI::Shell::implementation::NotificationAreaIcon2," in n
                 and f">::{member}(" in n)
    for key, member in (("rootAddRef", "AddRef"), ("rootRelease", "Release")):
        e.select(key, lambda n, member=member: "Microsoft::WRL::Details::RuntimeClassImpl<" in n and "struct ITrayUI," in n
                 and f">::{member}(" in n)
        e.select(key + "Thunk", lambda n, member=member: "Microsoft::WRL::Details::RuntimeClassImpl<" in n and "struct ITrayUI," in n
                 and f">::{member}`adjustor{{40}}'" in n)
    e.select("getIcon", lambda n: "produce<struct winrt::WindowsUdk::UI::Shell::implementation::NotificationAreaIcon2," in n
             and "INotificationAreaIcon>::get_Icon(" in n)
    e.select("visibleGetAt", lambda n: "winrt::impl::produce<" in n and "NotificationAreaIcon" in n
             and "IVector<struct winrt::WindowsUdk::UI::Shell::NotificationAreaIcon> >::GetAt(" in n)
    e.select("visibleGetAtImpl", lambda n: "winrt::vector_view_base<" in n and "NotificationAreaIcon>" in n and ">::GetAt(" in n)
    e.select("findIdentity", lambda n: " __cdecl std::find<" in n and "NotificationAreaIconIdentity" in n and "implementation::NotificationAreaIcon2" in n)

    ui_thread = e.symbol(lambda n: n == "unsigned long NotifyIconUtilities::g_tidUIThread")["rva"]
    root = e.symbol(lambda n: n.endswith(" g_trayUI") and "ComPtr<struct ITrayUI>" in n)["rva"]
    item_vtable = e.symbol(lambda n: n == "const winrt::WindowsUdk::UI::Shell::implementation::NotificationAreaIcon2::`vftable'")["rva"]
    item_slots = e.table("itemDefaultInterface", lambda n: "produce<struct winrt::WindowsUdk::UI::Shell::implementation::NotificationAreaIcon2," in n
                        and "INotificationAreaIcon>::`vftable'" in n, 15)
    root_slots = e.table("rootITrayUI", lambda n: n.startswith("const TrayUI::`vftable'{for `Microsoft::WRL::Details::ImplementsHelper<")
                        and ",1,struct ITrayUI,struct ITrayComponentHost,struct IStateCapture>'}" in n, 63)
    for slots, assignments in ((item_slots, {1: "itemAddRefThunk", 2: "itemRelease", 8: "getIcon"}),
                               (root_slots, {1: "rootAddRefThunk", 2: "rootReleaseThunk", 31: "copyData"})):
        for slot, key in assignments.items():
            require(slots[slot] == e.profiles[key]["rva"], f"{key}: private interface slot disagrees")

    # Identify the unnamed global teardown from exact typed-global xrefs and
    # the full pdata extent; no fixed RVA or wildcard instruction search.
    candidates, xrefs = [], []
    for start, end in e.functions.items():
        instructions = list(decoder.disasm(pe.get_data(start, end - start), start))
        for role, target in (("uiThread", ui_thread), ("root", root)):
            references = e.rip_references(instructions, target)
            xrefs.extend({"role": role, "functionRva": start, "instructionRva": item.address,
                          "instruction": f"{item.mnemonic} {item.op_str}"} for item in references)
            if role == "root" and end - start <= 96 and len(references) == 2:
                text = "\n".join(f"{item.mnemonic} {item.op_str}" for item in instructions)
                if re.search(r"mov rcx, qword ptr \[rip \+", text) and re.search(r"and qword ptr \[rip \+ [^\]]+\], 0", text):
                    candidates.append(start)
    require(len(candidates) == 1, "Global root teardown is missing or ambiguous")
    e.record("globalRootRelease", candidates[0], "Anonymous teardown of typed g_trayUI; pdata and global xrefs")
    e.verify("globalRootRelease", r"mov rax, qword ptr \[rcx\]\nmov rax, qword ptr \[rax \+ 0x10\]\ncall ")

    # Resolve the actual PE import. A same-shaped call to another function is
    # insufficient evidence for the thread identity used by the fail-fast.
    imported_thread = [entry.address - pe.OPTIONAL_HEADER.ImageBase
                       for library in pe.DIRECTORY_ENTRY_IMPORT for entry in library.imports
                       if entry.name == b"GetCurrentThreadId"]
    require(len(imported_thread) == 1, "GetCurrentThreadId import is missing or ambiguous")
    for key in ("failFastUiThread", "managerConstructor"):
        imports = e.rip_references(e.instructions[key], imported_thread[0])
        fields = e.rip_references(e.instructions[key], ui_thread)
        require(len(imports) == len(fields) == 1 and imports[0].mnemonic == "call", f"{key}: thread evidence differs")
        require(imports[0].address + imports[0].size == fields[0].address, f"{key}: thread result is not immediately used")
        require(fields[0].mnemonic == ("cmp" if key == "failFastUiThread" else "mov"), f"{key}: thread field operation differs")
        require("eax" in fields[0].op_str and "dword ptr" in fields[0].op_str, f"{key}: thread field width differs")
    for key in ("shellNotifyIcon", "addIcon", "deleteIcon", "modifyIcon", "managerDestructor", "getIcon"):
        e.verify(key, rf"call {e.profiles['failFastUiThread']['rva']:#x}\b")
    e.verify("makeManager", rf"call {e.profiles['managerConstructor']['rva']:#x}\b")
    e.verify("taskListInitialize", rf"call {e.profiles['makeManager']['rva']:#x}\b")

    # ITrayUI owns model at +270. Its HandleCopyData is slot 31 in the exact
    # vtable above, so these offsets are relative to that interface, not the
    # root implementation. Both independent native branches agree.
    field = r"(0x[0-9a-f]+)"
    routes = []
    for key in ("shellNotifyIcon", "getRect"):
        route = (rf"mov rdx, qword ptr \[rdi \+ {field}\]\ntest rdx, rdx\n(?:je|jne) [^\n]+\n"
                 rf"add rdx, {field}\nlea rcx, \[rbp - 0x20\]\ncall {e.profiles['sharedPtrCopy']['rva']:#x}\nnop\n"
                 rf"mov rdx, r8\nmov rcx, qword ptr \[rbp - 0x20\]\ncall {e.profiles[key]['rva']:#x}")
        matches = list(re.finditer(route, e.text("copyData")))
        require(len(matches) == 1, f"copyData: {key} chain is missing or ambiguous")
        routes.append(tuple(int(value, 16) for value in matches[0].groups()))
    require(routes[0] == routes[1], "Independent model/manager routes disagree")
    root_model, model_manager = routes[0]
    root_adjust = e.capture("rootAddRefThunk", rf"sub rcx, {field}\njmp {e.profiles['rootAddRef']['rva']:#x}")
    e.verify("rootReleaseThunk", rf"sub rcx, {root_adjust:#x}\njmp {e.profiles['rootRelease']['rva']:#x}")
    e.verify("rootConstructor", rf"mov qword ptr \[rdi \+ {root_model + root_adjust:#x}\], rbp")
    e.verify("rootDestructor", rf"lea rcx, \[rbx \+ {root_model + root_adjust:#x}\]\ncall ")
    e.verify("setManager", rf"mov qword ptr \[rdi \+ {model_manager + 16:#x}\], rdx")
    e.verify("taskListInitialize", rf"lea rdx, \[r8 - 0x10\][\s\S]*?call {e.profiles['setManager']['rva']:#x}")
    e.verify("sharedPtrCopy", r"lock inc dword ptr \[rax \+ 8\]")
    e.verify("sharedPtrCopy", r"mov rax, qword ptr \[rdx\]\nmov qword ptr \[rcx\], rax")
    e.verify("setManager", rf"call {e.profiles['sharedPtrCopy']['rva']:#x}\b")
    e.verify("setManager", rf"call {e.profiles['sharedPtrRelease']['rva']:#x}\b")

    item_adjust = e.capture("itemAddRefThunk", rf"lea rax, \[rcx - {field}\]")
    e.verify("itemAddRefThunk", rf"jmp {e.profiles['itemAddRef']['rva']:#x}")
    e.verify("itemRelease", rf"lea rax, \[rcx - {item_adjust:#x}\]")
    e.verify("itemRelease", rf"call {e.profiles['itemSubtractReference']['rva']:#x}")
    e.verify("itemSubtractReference", rf"jmp {e.profiles['itemWeakRelease']['rva']:#x}")
    e.verify("rootAddRef", rf"call {e.profiles['wrlAtomicAddRef']['rva']:#x}")
    e.verify("rootRelease", rf"call {e.profiles['wrlAtomicRelease']['rva']:#x}")
    # Ref acquisition must not pump messages or invoke an object callback.
    for key, permitted in (("itemAddRef", set()), ("wrlAtomicAddRef", set()),
                           ("rootAddRef", {e.profiles["wrlAtomicAddRef"]["rva"]})):
        from capstone.x86_const import X86_OP_IMM
        for item in e.instructions[key]:
            if item.mnemonic == "call":
                require(item.operands[0].type == X86_OP_IMM and item.operands[0].imm in permitted,
                        f"{key}: unexpected call during reference acquisition")
    icon_field = e.capture("getIcon", rf"lea rbx, \[rsi \+ {field}\]") + item_adjust
    e.verify("setIcon", rf"lea r14, \[rsi \+ {icon_field:#x}\][\s\S]*?mov qword ptr \[r14\], rbx")
    e.verify("getIcon", r"mov rax, qword ptr \[rbx\]\nmov rax, qword ptr \[rax \+ 8\]")
    e.verify("getIcon", r"mov qword ptr \[rdi\], rbx")

    window = e.capture("identity", rf"mov rax, qword ptr \[rcx \+ {field}\]\nmov rbx, rdx\nmov qword ptr \[rdx\], rax")
    identifier = e.capture("identity", rf"mov eax, dword ptr \[rcx \+ {field}\]\nmov dword ptr \[rdx \+ 8\], eax")
    guid = e.capture("identity", rf"movups xmm0, xmmword ptr \[rcx \+ {field}\]\nmovdqu xmmword ptr \[rdx \+ 0xc\], xmm0")
    require(identifier == window + 8 and guid == identifier + 4, "Identity fields disagree")
    e.verify("findIdentity", rf"mov rax, qword ptr \[rcx \+ {window:#x}\]")
    e.verify("findIdentity", rf"mov eax, dword ptr \[rcx \+ {identifier:#x}\]")
    e.verify("findIdentity", rf"movups xmm0, xmmword ptr \[rcx \+ {guid:#x}\]")
    e.verify("findIdentity", r"add rdi, 8\ncmp rdi, rbx")
    e.verify("getRect", rf"mov r8, qword ptr \[rbx \+ 8\]\nmov rdx, qword ptr \[rbx\][\s\S]*?call {e.profiles['findIdentity']['rva']:#x}")
    e.verify("addIcon", r"mov rdx, qword ptr \[rdi \+ 8\]\ncmp rdx, qword ptr \[rdi \+ 0x10\]")
    e.verify("addIcon", r"mov qword ptr \[rdx\], rbx[\s\S]*?add qword ptr \[rdi \+ 8\], 8")
    layout = {"uiThreadRva": ui_thread, "rootRva": root,
              "rootVtableRva": e.profiles["rootITrayUI"]["rva"], "rootModelOffset": root_model,
              "modelManagerOffset": model_manager, "managerBeginOffset": 0, "managerEndOffset": 8,
              "managerCapacityOffset": 16, "itemVtableRva": item_vtable,
              "itemInterfaceOffset": item_adjust, "itemInterfaceVtableRva": e.profiles["itemDefaultInterface"]["rva"],
              "itemIconSlot": 8, "itemWindowOffset": window, "itemIdOffset": identifier, "itemGuidOffset": guid}
    require(all(0 <= value < pe.OPTIONAL_HEADER.SizeOfImage for value in layout.values()), "Layout outside image bounds")
    return e, layout, xrefs


def c_bytes(raw):
    return "{ " + ", ".join(f"0x{value:02X}" for value in raw) + " }"


def header(image, evidence, layout):
    lines = ["/* Generated by scripts/Generate-TrayImageProfile.py from the reviewed",
             " * Microsoft PE/PDB. Hashes/layout only; no Microsoft instruction bytes.",
             " * Do not reuse this profile with a different system image. */",
             "#ifndef WINDOCK_TRAY_IMAGE_PROFILE_H", "#define WINDOCK_TRAY_IMAGE_PROFILE_H", "", "#include <stdint.h>", "",
             "typedef struct {", "    const char *name;", "    uint32_t rva, size;", "    uint8_t sha256[32];", "} TrayImageCodeEvidence;", "",
             "typedef struct {", "    uint32_t imageSize, machine, timestamp, fileLength;",
             "    uint8_t imageSha256[32], pdbGuid[16];", "    uint32_t pdbAge;"]
    lines += [f"    uint32_t {name};" for name in layout]
    lines += ["} TrayImageLayout;", "", "static const TrayImageLayout tray_image_layout = {",
              f"    .imageSize = 0x{image['sizeOfImage']:X}u, .machine = 0x{image['machine']:X}u,",
              f"    .timestamp = 0x{image['timeDateStamp']:X}u, .fileLength = {image['fileLength']}u,",
              f"    .imageSha256 = {c_bytes(bytes.fromhex(image['sha256']))},",
              f"    .pdbGuid = {c_bytes(uuid.UUID(image['pdbGuid']).bytes_le)}, .pdbAge = {image['pdbAge']}u,"]
    lines += [f"    .{name} = 0x{value:X}u," for name, value in layout.items()]
    lines += ["};", ""]
    for table, kind, count in (("tray_image_code", "function", "TRAY_IMAGE_CODE_COUNT"),
                               ("tray_image_vtables", "data", "TRAY_IMAGE_VTABLE_COUNT")):
        lines.append(f"static const TrayImageCodeEvidence {table}[] = {{")
        for name, value in evidence.profiles.items():
            if value["kind"] == kind:
                lines.append(f'    {{ "{name}", 0x{value["rva"]:X}u, {value["size"]}u, {c_bytes(bytes.fromhex(value["sha256"]))} }},')
        lines += ["};", f"#define {count} (sizeof({table}) / sizeof({table}[0]))", ""]
    lines += ["#endif /* WINDOCK_TRAY_IMAGE_PROFILE_H */", ""]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", type=Path, default=Path(r"C:\Windows\System32\Taskbar.dll"))
    parser.add_argument("--pdb", type=Path)
    parser.add_argument("--download", action="store_true", help="Download the exact PDB from Microsoft's symbol server")
    parser.add_argument("--decoder-path", type=Path, help="Existing directory containing pefile/capstone")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "publish/checks/tray-images")
    parser.add_argument("--header", type=Path, default=ROOT / "native/WinDock.TrayHook/tray_image_profile.h")
    parser.add_argument("--check", action="store_true", help="Compare the generated header without changing it")
    args = parser.parse_args()
    if args.decoder_path:
        sys.path.insert(0, str(args.decoder_path.resolve()))
    import pefile
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    raw_image = args.image.read_bytes()
    pe = pefile.PE(data=raw_image)
    image = model.image_identity(pe, raw_image)
    require(image["sha256"] == model.REVIEWED_IMAGE, "No reviewed extraction recipe for this image")
    pdb_key = uuid.UUID(image["pdbGuid"]).hex.upper() + format(image["pdbAge"], "X")
    url = f"{model.SYMBOL_SERVER}/{image['pdbName']}/{pdb_key}/{image['pdbName']}"
    pdb_path = args.pdb or ROOT / f"publish/checks/tray-model/Taskbar-{pdb_key}.pdb"
    if not pdb_path.exists():
        require(args.download, "Matching official PDB is missing; provide --pdb or --download")
        with urllib.request.urlopen(url, timeout=45) as response:
            raw = response.read(model.MAX_PDB_BYTES + 1)
        candidate = model.Pdb(raw)
        candidate.validate(image)
        require(model.sha256(raw) == REVIEWED_PDB, "Downloaded PDB differs from the reviewed official file")
        pdb_path.parent.mkdir(parents=True, exist_ok=True)
        temporary = pdb_path.with_suffix(".download")
        temporary.write_bytes(raw)
        temporary.replace(pdb_path)
    raw_pdb = pdb_path.read_bytes()
    pdb = model.Pdb(raw_pdb)
    pdb_info = pdb.validate(image)
    require(pdb_info["sha256"] == REVIEWED_PDB, "PDB differs from the reviewed official file")
    pdb_info["source"] = url
    public = pdb.symbols(pe)
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    decoder.detail = True
    evidence, layout, xrefs = build(pe, public, decoder)
    generated = header(image, evidence, layout)
    require(args.image.read_bytes() == raw_image and pdb_path.read_bytes() == raw_pdb, "Input changed during generation")
    if args.check:
        require(args.header.read_text(encoding="utf8") == generated, "Generated header differs")
    else:
        args.header.parent.mkdir(parents=True, exist_ok=True)
        args.header.write_text(generated, encoding="utf8", newline="\n")
    args.output_dir.mkdir(parents=True, exist_ok=True)
    document = {"image": image, "pdb": pdb_info, "layout": layout, "symbols": evidence.profiles,
                "globalXrefs": xrefs, "headerSha256": hashlib.sha256(generated.encode()).hexdigest(),
                "limits": {"offlineOnly": True, "comExecuted": False, "processInspected": False, "desktopTested": False}}
    (args.output_dir / "generated-image-evidence.json").write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf8")
    (args.output_dir / "generated-image-disassembly.txt").write_text(evidence.disassembly(), encoding="utf8")
    print(json.dumps({"header": str(args.header.resolve()), "imageSha256": image["sha256"],
                      "headerSha256": document["headerSha256"], "functions": sum(v["kind"] == "function" for v in evidence.profiles.values()),
                      "functionBytes": sum(v["size"] for v in evidence.profiles.values() if v["kind"] == "function"),
                      "vtables": sum(v["kind"] == "data" for v in evidence.profiles.values()), "checked": args.check}))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, struct.error, ImportError) as error:
        print(f"No image profile generated: {error}", file=sys.stderr)
        sys.exit(1)
