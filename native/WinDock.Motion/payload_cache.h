#pragma once
#include "image_identity.h"
#include "payload_hash.h"
#include <aclapi.h>
#include <sddl.h>
#include <shlobj.h>
#include <string>

namespace motion {
struct Handle {
    HANDLE value = nullptr;
    explicit Handle(HANDLE handle = nullptr) : value(handle) {}
    ~Handle() { Reset(); }
    void Reset(HANDLE handle = nullptr) {
        const auto error = GetLastError();
        if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value);
        value = handle;
        SetLastError(error);
    }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

class PayloadCache {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    PSID compositor = nullptr;
    Handle root, version, file, host;
    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, FALSE};

    bool Trusted(HANDLE handle, bool directory) {
        BY_HANDLE_FILE_INFORMATION info{};
        if (!GetFileInformationByHandle(handle, &info)) return false;
        if ((info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)
            || ((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) != directory
            || (!directory && info.nNumberOfLinks != 1)) { SetLastError(ERROR_ACCESS_DENIED); return false; }
        PSID owner = nullptr;
        PACL acl = nullptr;
        PSECURITY_DESCRIPTOR actual = nullptr;
        const auto error = GetSecurityInfo(handle, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            &owner, nullptr, &acl, nullptr, &actual);
        if (error != ERROR_SUCCESS) { SetLastError(error); return false; }
        SECURITY_DESCRIPTOR_CONTROL control{};
        DWORD revision = 0;
        bool ok = owner && (IsWellKnownSid(owner, WinBuiltinAdministratorsSid) || IsWellKnownSid(owner, WinLocalSystemSid))
            && acl && GetSecurityDescriptorControl(actual, &control, &revision) && (control & SE_DACL_PROTECTED);
        bool system = false, admin = false, reader = false;
        for (DWORD i = 0; ok && i < acl->AceCount; ++i) {
            void* address = nullptr;
            if (!GetAce(acl, i, &address)) { ok = false; break; }
            const auto ace = static_cast<ACCESS_ALLOWED_ACE*>(address);
            if (ace->Header.AceType != ACCESS_ALLOWED_ACE_TYPE || (ace->Header.AceFlags & INHERIT_ONLY_ACE)) { ok = false; break; }
            auto sid = const_cast<DWORD*>(&ace->SidStart);
            if (IsWellKnownSid(sid, WinLocalSystemSid)) system |= ace->Mask == FILE_ALL_ACCESS;
            else if (IsWellKnownSid(sid, WinBuiltinAdministratorsSid)) admin |= ace->Mask == FILE_ALL_ACCESS;
            else if (EqualSid(sid, compositor) && ace->Mask == (FILE_GENERIC_READ | FILE_GENERIC_EXECUTE)) reader = true;
            else ok = false;
        }
        LocalFree(actual);
        if (!ok || !system || !admin || !reader) { SetLastError(ERROR_ACCESS_DENIED); return false; }
        return true;
    }

    bool Directory(const std::wstring& path, Handle& handle) {
        if (!CreateDirectoryW(path.c_str(), &security) && GetLastError() != ERROR_ALREADY_EXISTS) return false;
        handle.Reset(CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES | READ_CONTROL, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        return handle.value != INVALID_HANDLE_VALUE && Trusted(handle.value, true);
    }

    bool Copy(HANDLE source, HANDLE target) {
        LARGE_INTEGER start{};
        if (!SetFilePointerEx(source, start, nullptr, FILE_BEGIN)) return false;
        BYTE buffer[65536];
        for (;;) {
            DWORD count = 0, written = 0;
            if (!ReadFile(source, buffer, sizeof(buffer), &count, nullptr)) return false;
            if (!count) break;
            if (!WriteFile(target, buffer, count, &written, nullptr)) return false;
            if (written != count) { SetLastError(ERROR_WRITE_FAULT); return false; }
        }
        return FlushFileBuffers(target) != FALSE;
    }

public:
    std::wstring path;
    std::wstring hostPath;
    ~PayloadCache() { if (descriptor) LocalFree(descriptor); if (compositor) LocalFree(compositor); }

    bool Prepare(HANDLE source) {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;S-1-5-90-0)",
            SDDL_REVISION_1, &descriptor, nullptr) || !ConvertStringSidToSidW(L"S-1-5-90-0", &compositor)) return false;
        security.lpSecurityDescriptor = descriptor;
        PWSTR known = nullptr;
        const auto result = SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &known);
        if (FAILED(result)) { if (known) CoTaskMemFree(known); SetLastError(static_cast<DWORD>(result)); return false; }
        std::wstring directory = std::wstring(known) + L"\\WinDock.NativeMotion";
        CoTaskMemFree(known);
        if (!Directory(directory, root)) return false;
        directory += L"\\";
        for (const char* p = WIN_DOCK_MOTION_PAYLOAD_SHA256; *p; ++p) directory += static_cast<wchar_t>(*p);
        if (!Directory(directory, version)) return false;
        path = directory + L"\\WinDock.Motion.x64.dll";
        {
            Handle created(CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, &security,
                CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
            if (created.value == INVALID_HANDLE_VALUE) {
                if (GetLastError() != ERROR_FILE_EXISTS && GetLastError() != ERROR_ALREADY_EXISTS) return false;
            } else if (!Copy(source, created.value)) {
                const auto error = GetLastError();
                created.Reset();
                DeleteFileW(path.c_str());
                SetLastError(error); return false;
            }
        }
        file.Reset(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (file.value == INVALID_HANDLE_VALUE || !Trusted(file.value, false)) return false;
        char digest[65];
        if (!HashFile(file.value, digest) || strcmp(digest, WIN_DOCK_MOTION_PAYLOAD_SHA256) != 0) {
            SetLastError(ERROR_INVALID_DATA); return false;
        }
        return true;
    }

    bool PrepareHost(const BYTE* bytes, DWORD length, const char* expected) {
        if (path.empty() || !descriptor) { SetLastError(ERROR_INVALID_STATE); return false; }
        hostPath = path.substr(0, path.find_last_of(L'\\') + 1) + L"WinDock.MotionLogin.";
        for (const char* p = expected; *p; ++p) hostPath += static_cast<wchar_t>(*p);
        hostPath += L".exe";
        {
            Handle created(CreateFileW(hostPath.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, &security,
                CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
            if (created.value == INVALID_HANDLE_VALUE) {
                if (GetLastError() != ERROR_FILE_EXISTS && GetLastError() != ERROR_ALREADY_EXISTS) return false;
            } else {
                DWORD written = 0;
                const bool copied = WriteFile(created.value, bytes, length, &written, nullptr)
                    && written == length && FlushFileBuffers(created.value);
                if (!copied) {
                const auto error = GetLastError();
                created.Reset(); DeleteFileW(hostPath.c_str());
                SetLastError(error); return false;
                }
            }
        }
        host.Reset(CreateFileW(hostPath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (host.value == INVALID_HANDLE_VALUE || !Trusted(host.value, false)) return false;
        char actual[65];
        if (!HashFile(host.value, actual) || strcmp(expected, actual) != 0) {
            SetLastError(ERROR_INVALID_DATA); return false;
        }
        return true;
    }

    bool VerifyHost(const std::wstring& ownPath) {
        const auto directory = path.substr(0, path.find_last_of(L'\\') + 1);
        const auto prefix = directory + L"WinDock.MotionLogin.";
        if (ownPath.size() != prefix.size() + 64 + 4 || _wcsnicmp(ownPath.c_str(), prefix.c_str(), prefix.size()) != 0
            || _wcsicmp(ownPath.c_str() + ownPath.size() - 4, L".exe") != 0) {
            SetLastError(ERROR_ACCESS_DENIED); return false;
        }
        host.Reset(CreateFileW(ownPath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (host.value == INVALID_HANDLE_VALUE || !Trusted(host.value, false)) return false;
        char actual[65];
        if (!HashFile(host.value, actual)) return false;
        for (unsigned i = 0; i < 64; ++i)
            if (ownPath[prefix.size() + i] != static_cast<wchar_t>(actual[i])) {
                SetLastError(ERROR_INVALID_DATA); return false;
            }
        return true;
    }
};
}
