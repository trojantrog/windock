#pragma once
#include <windows.h>
#include <bcrypt.h>
#include <stdint.h>
#include <string.h>

namespace motion {
constexpr char SupportedImageHash[] = "de3a7bacdcbb49a98bfff41010e772d18a329b77d9bc6b2416e1bc9f5b415863";

inline bool HashFile(HANDLE file, char output[65]) {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    bool ok = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0;
    if (ok) ok = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) == 0;
    BYTE bytes[65536], digest[32];
    DWORD read = 0;
    LARGE_INTEGER zero{};
    if (ok) ok = SetFilePointerEx(file, zero, nullptr, FILE_BEGIN) != FALSE;
    while (ok) {
        if (!ReadFile(file, bytes, sizeof(bytes), &read, nullptr)) { ok = false; break; }
        if (read == 0) break;
        ok = BCryptHashData(hash, bytes, read, 0) == 0;
    }
    if (ok) ok = BCryptFinishHash(hash, digest, sizeof(digest), 0) == 0;
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    if (ok) {
        constexpr char hex[] = "0123456789abcdef";
        for (unsigned i = 0; i < 32; ++i) {
            output[i * 2] = hex[digest[i] >> 4];
            output[i * 2 + 1] = hex[digest[i] & 15];
        }
        output[64] = 0;
    }
    return ok;
}

inline bool SupportedModule(HMODULE module) {
    wchar_t path[32768];
    if (!GetModuleFileNameW(module, path, 32768)) return false;
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    char digest[65];
    bool ok = HashFile(file, digest) && strcmp(digest, SupportedImageHash) == 0;
    IMAGE_DOS_HEADER dos{};
    IMAGE_NT_HEADERS64 nt{};
    DWORD read = 0;
    LARGE_INTEGER position{};
    if (ok) ok = SetFilePointerEx(file, position, nullptr, FILE_BEGIN) && ReadFile(file, &dos, sizeof(dos), &read, nullptr)
        && read == sizeof(dos) && dos.e_magic == IMAGE_DOS_SIGNATURE && dos.e_lfanew > 0 && dos.e_lfanew < 65536;
    position.QuadPart = dos.e_lfanew;
    if (ok) ok = SetFilePointerEx(file, position, nullptr, FILE_BEGIN) && ReadFile(file, &nt, sizeof(nt), &read, nullptr)
        && read == sizeof(nt) && nt.Signature == IMAGE_NT_SIGNATURE && nt.OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC;
    if (ok) {
        const auto memoryDos = reinterpret_cast<const IMAGE_DOS_HEADER*>(module);
        ok = memoryDos->e_magic == dos.e_magic && memoryDos->e_lfanew == dos.e_lfanew;
    }
    if (ok) {
        const auto memoryNt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(reinterpret_cast<const BYTE*>(module) + dos.e_lfanew);
        ok = memoryNt->Signature == nt.Signature
            && memoryNt->FileHeader.TimeDateStamp == nt.FileHeader.TimeDateStamp
            && memoryNt->OptionalHeader.SizeOfImage == nt.OptionalHeader.SizeOfImage
            && memoryNt->OptionalHeader.CheckSum == nt.OptionalHeader.CheckSum;
    }
    CloseHandle(file);
    return ok;
}
}
