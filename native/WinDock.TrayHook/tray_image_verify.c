#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>
#include "tray_image_verify.h"
#include "tray_image_profile.h"

#define IMAGE_RANGE_COUNT (TRAY_IMAGE_CODE_COUNT + TRAY_IMAGE_VTABLE_COUNT)
#define MAX_PROFILE_FILE (16u * 1024u * 1024u)
#define MAX_PE_SECTIONS 96u
#define MEMORY_CHUNK 1024u

_Static_assert(sizeof(uintptr_t) == 8, "The reviewed tray-image profile requires AMD64");

typedef struct {
    uintptr_t address;
    SIZE_T offset;
    DWORD bytes;
} VerifiedRange;

struct TrayImageProfile {
    HMODULE module;
    uintptr_t base;
    BYTE *expected;
    SIZE_T expectedBytes;
    SIZE_T rangeCount;
    VerifiedRange ranges[IMAGE_RANGE_COUNT];
};

typedef struct {
    const BYTE *bytes;
    SIZE_T length;
    DWORD ntOffset;
    DWORD sectionOffset;
    IMAGE_NT_HEADERS64 nt;
    IMAGE_SECTION_HEADER sections[MAX_PE_SECTIONS];
} DiskImage;

static BOOL SpanFits(SIZE_T total, SIZE_T offset, SIZE_T bytes) {
    return offset <= total && bytes <= total - offset;
}

BOOL TrayImageReadMemory(uintptr_t address, void *output, SIZE_T bytes) {
    SIZE_T copied = 0;
    if (!output || !bytes || bytes > 4096 || address < 0x10000 ||
        address > (uintptr_t)INTPTR_MAX || bytes > (uintptr_t)INTPTR_MAX - address)
        return FALSE;
    return ReadProcessMemory(GetCurrentProcess(), (LPCVOID)address, output, bytes, &copied) && copied == bytes;
}

static BOOL HashEquals(BCRYPT_ALG_HANDLE algorithm, const BYTE *bytes, SIZE_T count, const BYTE expected[32]) {
    BYTE digest[32];
    BCRYPT_HASH_HANDLE hash = NULL;
    BOOL ok = FALSE;
    if (!bytes || count > MAXDWORD || BCryptCreateHash(algorithm, &hash, NULL, 0, NULL, 0, 0) < 0)
        return FALSE;
    if (BCryptHashData(hash, (PUCHAR)bytes, (ULONG)count, 0) >= 0 &&
        BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0)
        ok = memcmp(digest, expected, sizeof(digest)) == 0;
    BCryptDestroyHash(hash);
    return ok;
}

static BOOL ReadDiskImage(HMODULE module, BYTE **result) {
    WCHAR actual[MAX_PATH], system[MAX_PATH], finalPath[MAX_PATH + 8];
    DWORD actualLength = GetModuleFileNameW(module, actual, ARRAYSIZE(actual));
    UINT systemLength = GetSystemDirectoryW(system, ARRAYSIZE(system));
    const WCHAR suffix[] = L"\\Taskbar.dll";
    HANDLE file = INVALID_HANDLE_VALUE;
    BYTE *bytes = NULL;
    LARGE_INTEGER length;
    BOOL ok = FALSE;
    *result = NULL;
    if (!actualLength || actualLength >= ARRAYSIZE(actual) || !systemLength ||
        systemLength + ARRAYSIZE(suffix) > ARRAYSIZE(system))
        return FALSE;
    memcpy(system + systemLength, suffix, sizeof(suffix));
    if (_wcsicmp(actual, system) != 0)
        return FALSE;
    /* Keep this exact file stable while hashing/parsing; no write/delete share.
     * A servicing conflict simply makes the image capability unavailable. */
    file = CreateFileW(system, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING,
                       FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (file == INVALID_HANDLE_VALUE)
        return FALSE;
    DWORD finalLength = GetFinalPathNameByHandleW(file, finalPath, ARRAYSIZE(finalPath), FILE_NAME_NORMALIZED);
    if (!finalLength || finalLength >= ARRAYSIZE(finalPath) || wcsncmp(finalPath, L"\\\\?\\", 4) != 0 ||
        _wcsicmp(finalPath + 4, system) != 0 || !GetFileSizeEx(file, &length) ||
        length.QuadPart != tray_image_layout.fileLength || length.QuadPart <= 0 || length.QuadPart > MAX_PROFILE_FILE)
        goto done;
    bytes = HeapAlloc(GetProcessHeap(), 0, (SIZE_T)length.QuadPart);
    if (!bytes)
        goto done;
    SIZE_T at = 0;
    while (at < (SIZE_T)length.QuadPart) {
        DWORD requested = (DWORD)(((SIZE_T)length.QuadPart - at > 65536) ? 65536 : (SIZE_T)length.QuadPart - at);
        DWORD copied = 0;
        if (!ReadFile(file, bytes + at, requested, &copied, NULL) || copied != requested)
            goto done;
        at += copied;
    }
    *result = bytes;
    bytes = NULL;
    ok = TRUE;
done:
    if (bytes) HeapFree(GetProcessHeap(), 0, bytes);
    CloseHandle(file);
    return ok;
}

static BOOL ParseDiskImage(const BYTE *bytes, SIZE_T length, DiskImage *image) {
    IMAGE_DOS_HEADER dos;
    if (!SpanFits(length, 0, sizeof(dos))) return FALSE;
    memcpy(&dos, bytes, sizeof(dos));
    if (dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < (LONG)sizeof(dos) ||
        !SpanFits(length, (DWORD)dos.e_lfanew, sizeof(image->nt))) return FALSE;
    memset(image, 0, sizeof(*image));
    image->bytes = bytes;
    image->length = length;
    image->ntOffset = (DWORD)dos.e_lfanew;
    memcpy(&image->nt, bytes + image->ntOffset, sizeof(image->nt));
    const IMAGE_FILE_HEADER *file = &image->nt.FileHeader;
    const IMAGE_OPTIONAL_HEADER64 *optional = &image->nt.OptionalHeader;
    if (image->nt.Signature != IMAGE_NT_SIGNATURE || file->Machine != tray_image_layout.machine ||
        file->TimeDateStamp != tray_image_layout.timestamp || !file->NumberOfSections ||
        file->NumberOfSections > MAX_PE_SECTIONS || file->SizeOfOptionalHeader != sizeof(*optional) ||
        optional->Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC || optional->SizeOfImage != tray_image_layout.imageSize ||
        optional->SizeOfHeaders > length || optional->NumberOfRvaAndSizes != IMAGE_NUMBEROF_DIRECTORY_ENTRIES)
        return FALSE;
    image->sectionOffset = image->ntOffset + sizeof(DWORD) + sizeof(IMAGE_FILE_HEADER) + file->SizeOfOptionalHeader;
    SIZE_T sectionBytes = (SIZE_T)file->NumberOfSections * sizeof(IMAGE_SECTION_HEADER);
    if (!SpanFits(length, image->sectionOffset, sectionBytes) ||
        !SpanFits(optional->SizeOfHeaders, image->sectionOffset, sectionBytes) ||
        optional->SizeOfHeaders > optional->SizeOfImage) return FALSE;
    memcpy(image->sections, bytes + image->sectionOffset, sectionBytes);
    for (WORD i = 0; i < file->NumberOfSections; ++i) {
        const IMAGE_SECTION_HEADER *section = &image->sections[i];
        if (!SpanFits(length, section->PointerToRawData, section->SizeOfRawData) ||
            !SpanFits(optional->SizeOfImage, section->VirtualAddress, section->Misc.VirtualSize))
            return FALSE;
    }
    return TRUE;
}

static const BYTE *AtRva(const DiskImage *image, DWORD rva, SIZE_T count) {
    if (!SpanFits(image->nt.OptionalHeader.SizeOfImage, rva, count)) return NULL;
    if (SpanFits(image->nt.OptionalHeader.SizeOfHeaders, rva, count) && SpanFits(image->length, rva, count))
        return image->bytes + rva;
    for (WORD i = 0; i < image->nt.FileHeader.NumberOfSections; ++i) {
        const IMAGE_SECTION_HEADER *section = &image->sections[i];
        if (rva < section->VirtualAddress) continue;
        SIZE_T delta = rva - section->VirtualAddress;
        if (SpanFits(section->SizeOfRawData, delta, count) &&
            SpanFits(image->length, section->PointerToRawData + delta, count))
            return image->bytes + section->PointerToRawData + delta;
    }
    return NULL;
}

static BOOL IsExecutable(const DiskImage *image, DWORD rva, DWORD count) {
    for (WORD i = 0; i < image->nt.FileHeader.NumberOfSections; ++i) {
        const IMAGE_SECTION_HEADER *section = &image->sections[i];
        if ((section->Characteristics & IMAGE_SCN_MEM_EXECUTE) && rva >= section->VirtualAddress &&
            SpanFits(section->Misc.VirtualSize, rva - section->VirtualAddress, count)) return TRUE;
    }
    return FALSE;
}

static BOOL HasDir64Relocation(const DiskImage *image, DWORD target) {
    const IMAGE_DATA_DIRECTORY directory = image->nt.OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_BASERELOC];
    const BYTE *bytes = AtRva(image, directory.VirtualAddress, directory.Size);
    if (!bytes || !directory.Size) return FALSE;
    SIZE_T at = 0;
    while (at < directory.Size) {
        IMAGE_BASE_RELOCATION block;
        if (!SpanFits(directory.Size, at, sizeof(block))) return FALSE;
        memcpy(&block, bytes + at, sizeof(block));
        if (block.SizeOfBlock < sizeof(block) || (block.SizeOfBlock - sizeof(block)) % sizeof(WORD) ||
            !SpanFits(directory.Size, at, block.SizeOfBlock)) return FALSE;
        for (SIZE_T offset = sizeof(block); offset < block.SizeOfBlock; offset += sizeof(WORD)) {
            WORD entry;
            memcpy(&entry, bytes + at + offset, sizeof(entry));
            if ((entry >> 12) == IMAGE_REL_BASED_DIR64 && block.VirtualAddress <= target &&
                target - block.VirtualAddress == (entry & 0x0fff)) return TRUE;
        }
        at += block.SizeOfBlock;
    }
    return FALSE;
}

static BOOL MemoryEquals(uintptr_t address, const BYTE *expected, SIZE_T count) {
    BYTE current[MEMORY_CHUNK];
    if (address > (uintptr_t)INTPTR_MAX || count > (uintptr_t)INTPTR_MAX - address) return FALSE;
    for (SIZE_T at = 0; at < count;) {
        SIZE_T bytes = count - at > sizeof(current) ? sizeof(current) : count - at;
        if (!TrayImageReadMemory(address + at, current, bytes) || memcmp(current, expected + at, bytes) != 0)
            return FALSE;
        at += bytes;
    }
    return TRUE;
}

static BOOL ValidateLoadedIdentity(const DiskImage *image, uintptr_t base) {
    IMAGE_DOS_HEADER dos;
    IMAGE_NT_HEADERS64 nt;
    if (!TrayImageReadMemory(base, &dos, sizeof(dos)) || dos.e_magic != IMAGE_DOS_SIGNATURE ||
        dos.e_lfanew != (LONG)image->ntOffset ||
        !TrayImageReadMemory(base + image->ntOffset, &nt, sizeof(nt)) ||
        nt.Signature != image->nt.Signature ||
        memcmp(&nt.FileHeader, &image->nt.FileHeader, sizeof(nt.FileHeader)) != 0 ||
        nt.OptionalHeader.Magic != image->nt.OptionalHeader.Magic ||
        nt.OptionalHeader.SizeOfImage != image->nt.OptionalHeader.SizeOfImage ||
        nt.OptionalHeader.SizeOfHeaders != image->nt.OptionalHeader.SizeOfHeaders ||
        nt.OptionalHeader.NumberOfRvaAndSizes != image->nt.OptionalHeader.NumberOfRvaAndSizes ||
        memcmp(&nt.OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG],
               &image->nt.OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG], sizeof(IMAGE_DATA_DIRECTORY)) != 0 ||
        !MemoryEquals(base + image->sectionOffset, (const BYTE *)image->sections,
                      (SIZE_T)image->nt.FileHeader.NumberOfSections * sizeof(IMAGE_SECTION_HEADER)))
        return FALSE;
    const IMAGE_DATA_DIRECTORY directory = image->nt.OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG];
    const BYTE *debug = AtRva(image, directory.VirtualAddress, directory.Size);
    if (!debug || !directory.Size || directory.Size % sizeof(IMAGE_DEBUG_DIRECTORY) || directory.Size > 4096 ||
        !MemoryEquals(base + directory.VirtualAddress, debug, directory.Size)) return FALSE;
    unsigned found = 0;
    for (SIZE_T at = 0; at < directory.Size; at += sizeof(IMAGE_DEBUG_DIRECTORY)) {
        IMAGE_DEBUG_DIRECTORY entry;
        memcpy(&entry, debug + at, sizeof(entry));
        if (entry.Type != IMAGE_DEBUG_TYPE_CODEVIEW) continue;
        const BYTE *record = AtRva(image, entry.AddressOfRawData, entry.SizeOfData);
        if (!record || entry.SizeOfData < 25 || entry.SizeOfData > 4096 || memcmp(record, "RSDS", 4) != 0 ||
            memcmp(record + 4, tray_image_layout.pdbGuid, 16) != 0) return FALSE;
        DWORD age;
        memcpy(&age, record + 20, sizeof(age));
        if (age != tray_image_layout.pdbAge || !memchr(record + 24, 0, entry.SizeOfData - 24) ||
            !MemoryEquals(base + entry.AddressOfRawData, record, entry.SizeOfData)) return FALSE;
        ++found;
    }
    return found == 1;
}

static BOOL AddRange(TrayImageProfile *profile, const DiskImage *image, BCRYPT_ALG_HANDLE algorithm,
                     const TrayImageCodeEvidence *proof, BOOL table) {
    const BYTE *bytes = AtRva(image, proof->rva, proof->size);
    if (!bytes || !proof->size || proof->size > 16384 || profile->rangeCount >= IMAGE_RANGE_COUNT ||
        !SpanFits(profile->expectedBytes, profile->ranges[profile->rangeCount].offset, proof->size) ||
        !HashEquals(algorithm, bytes, proof->size, proof->sha256)) return FALSE;
    SIZE_T offset = profile->ranges[profile->rangeCount].offset;
    BYTE *expected = profile->expected + offset;
    if (table) {
        if (proof->size % sizeof(uint64_t)) return FALSE;
        for (DWORD at = 0; at < proof->size; at += sizeof(uint64_t)) {
            uint64_t preferred;
            memcpy(&preferred, bytes + at, sizeof(preferred));
            if (preferred < image->nt.OptionalHeader.ImageBase ||
                preferred - image->nt.OptionalHeader.ImageBase >= image->nt.OptionalHeader.SizeOfImage ||
                !IsExecutable(image, (DWORD)(preferred - image->nt.OptionalHeader.ImageBase), 1) ||
                !HasDir64Relocation(image, proof->rva + at)) return FALSE;
            uintptr_t relocated = profile->base + (uintptr_t)(preferred - image->nt.OptionalHeader.ImageBase);
            memcpy(expected + at, &relocated, sizeof(relocated));
        }
    } else {
        if (!IsExecutable(image, proof->rva, proof->size)) return FALSE;
        memcpy(expected, bytes, proof->size);
    }
    VerifiedRange *range = &profile->ranges[profile->rangeCount++];
    range->address = profile->base + proof->rva;
    range->bytes = proof->size;
    if (profile->rangeCount < IMAGE_RANGE_COUNT)
        profile->ranges[profile->rangeCount].offset = offset + proof->size;
    return TRUE;
}

TrayImageProfile *TrayImageProfileCreate(void) {
    HMODULE module = NULL;
    BYTE *diskBytes = NULL;
    BCRYPT_ALG_HANDLE algorithm = NULL;
    TrayImageProfile *profile = NULL;
    BOOL ok = FALSE;
    DiskImage disk;
    if (!GetModuleHandleExW(0, L"Taskbar.dll", &module)) return NULL;
    if (!ReadDiskImage(module, &diskBytes) ||
        BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0) < 0 ||
        !HashEquals(algorithm, diskBytes, tray_image_layout.fileLength, tray_image_layout.imageSha256) ||
        !ParseDiskImage(diskBytes, tray_image_layout.fileLength, &disk) ||
        (uintptr_t)module > (uintptr_t)INTPTR_MAX - tray_image_layout.imageSize ||
        !ValidateLoadedIdentity(&disk, (uintptr_t)module)) goto done;
    profile = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(*profile));
    if (!profile) goto done;
    profile->module = module;
    module = NULL;
    profile->base = (uintptr_t)profile->module;
    for (SIZE_T i = 0; i < TRAY_IMAGE_CODE_COUNT; ++i) profile->expectedBytes += tray_image_code[i].size;
    for (SIZE_T i = 0; i < TRAY_IMAGE_VTABLE_COUNT; ++i) profile->expectedBytes += tray_image_vtables[i].size;
    if (!profile->expectedBytes || profile->expectedBytes > 65536) goto done;
    profile->expected = HeapAlloc(GetProcessHeap(), 0, profile->expectedBytes);
    if (!profile->expected) goto done;
    for (SIZE_T i = 0; i < TRAY_IMAGE_CODE_COUNT; ++i)
        if (!AddRange(profile, &disk, algorithm, &tray_image_code[i], FALSE)) goto done;
    for (SIZE_T i = 0; i < TRAY_IMAGE_VTABLE_COUNT; ++i)
        if (!AddRange(profile, &disk, algorithm, &tray_image_vtables[i], TRUE)) goto done;
    if (profile->rangeCount != IMAGE_RANGE_COUNT || !TrayImageProfileValidateLive(profile)) goto done;
    ok = TRUE;
done:
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    if (diskBytes) HeapFree(GetProcessHeap(), 0, diskBytes);
    if (module) FreeLibrary(module);
    if (!ok) {
        TrayImageProfileDestroy(profile);
        profile = NULL;
    }
    return profile;
}

BOOL TrayImageProfileValidateLive(const TrayImageProfile *profile) {
    if (!profile || !profile->expected || profile->rangeCount != IMAGE_RANGE_COUNT) return FALSE;
    for (SIZE_T i = 0; i < profile->rangeCount; ++i) {
        const VerifiedRange *range = &profile->ranges[i];
        if (!SpanFits(profile->expectedBytes, range->offset, range->bytes) ||
            !MemoryEquals(range->address, profile->expected + range->offset, range->bytes)) return FALSE;
    }
    return TRUE;
}

uintptr_t TrayImageProfileBase(const TrayImageProfile *profile) {
    return profile ? profile->base : 0;
}

void TrayImageProfileDestroy(TrayImageProfile *profile) {
    if (!profile) return;
    HMODULE module = profile->module;
    if (profile->expected) HeapFree(GetProcessHeap(), 0, profile->expected);
    HeapFree(GetProcessHeap(), 0, profile);
    if (module) FreeLibrary(module);
}
