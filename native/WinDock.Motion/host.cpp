#include "protocol.h"
#include "image_identity.h"
#include "payload_hash.h"
#include <tlhelp32.h>
#include <string>
#include <cwchar>

namespace {
struct Handle {
    HANDLE value = nullptr;
    explicit Handle(HANDLE handle = nullptr) : value(handle) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

bool EnableDebugPrivilege() {
    Handle token;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token.value)) return false;
    TOKEN_PRIVILEGES privilege{};
    privilege.PrivilegeCount = 1;
    privilege.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    if (!LookupPrivilegeValueW(nullptr, SE_DEBUG_NAME, &privilege.Privileges[0].Luid)) return false;
    SetLastError(ERROR_SUCCESS);
    return AdjustTokenPrivileges(token.value, FALSE, &privilege, 0, nullptr, nullptr) && GetLastError() == ERROR_SUCCESS;
}

DWORD FindDwm(DWORD session) {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (snapshot.value == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W entry{sizeof(entry)};
    for (BOOL found = Process32FirstW(snapshot.value, &entry); found; found = Process32NextW(snapshot.value, &entry)) {
        DWORD current = 0;
        if (_wcsicmp(entry.szExeFile, L"dwm.exe") == 0 && ProcessIdToSessionId(entry.th32ProcessID, &current) && current == session)
            return entry.th32ProcessID;
    }
    return 0;
}

uintptr_t RemoteModule(DWORD pid, const wchar_t* name) {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
    if (snapshot.value == INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W entry{sizeof(entry)};
    for (BOOL found = Module32FirstW(snapshot.value, &entry); found; found = Module32NextW(snapshot.value, &entry))
        if (_wcsicmp(entry.szModule, name) == 0) return reinterpret_cast<uintptr_t>(entry.modBaseAddr);
    return 0;
}

int Run() {
    DWORD session = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session) || session == 0) return 10;
    wchar_t mappingName[128];
    swprintf_s(mappingName, L"%ls%lu", motion::ConfigPrefix, session);
    Handle mapping(OpenFileMappingW(FILE_MAP_READ, FALSE, mappingName));
    if (!mapping.value) return 11;
    auto config = static_cast<const motion::Config*>(MapViewOfFile(mapping.value, FILE_MAP_READ, 0, 0, sizeof(motion::Config)));
    if (!config) return 11;
    const bool requested = config->magic == motion::Magic && config->version == motion::Protocol
        && config->bytes == sizeof(motion::Config) && (config->flags & motion::Enabled)
        && GetTickCount64() >= config->heartbeat && GetTickCount64() - config->heartbeat < 2000;
    UnmapViewOfFile(config);
    if (!requested) return 12;
    wchar_t ownPath[32768];
    if (!GetModuleFileNameW(nullptr, ownPath, 32768)) return 13;
    std::wstring directory(ownPath);
    const auto slash = directory.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return 13;
    directory.resize(slash + 1);
    const auto payload = directory + L"WinDock.Motion.x64.dll";
    Handle file(CreateFileW(payload.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    char digest[65];
    if (file.value == INVALID_HANDLE_VALUE || !motion::HashFile(file.value, digest) || strcmp(digest, WIN_DOCK_MOTION_PAYLOAD_SHA256) != 0) return 14;
    if (!EnableDebugPrivilege()) return 15;
    const DWORD pid = FindDwm(session);
    if (!pid) return 16;
    Handle process(OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid));
    if (!process.value) return 17;
    wchar_t processPath[32768]; DWORD processLength = 32768;
    if (!QueryFullProcessImageNameW(process.value, 0, processPath, &processLength)) return 18;
    wchar_t system[32768];
    if (!GetSystemDirectoryW(system, 32768)) return 18;
    if (_wcsicmp(processPath, (std::wstring(system) + L"\\dwm.exe").c_str()) != 0) return 18;
    if (RemoteModule(pid, L"WinDock.Motion.x64.dll")) return 0;
    auto load = reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
    HMODULE localModule = nullptr;
    if (!load || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(load), &localModule)) return 19;
    wchar_t modulePath[32768];
    if (!GetModuleFileNameW(localModule, modulePath, 32768)) return 19;
    const wchar_t* moduleName = wcsrchr(modulePath, L'\\');
    if (!moduleName) return 19;
    const auto remoteBase = RemoteModule(pid, moduleName + 1);
    if (!remoteBase) return 19;
    const auto remoteLoad = reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteBase + reinterpret_cast<uintptr_t>(load) - reinterpret_cast<uintptr_t>(localModule));
    const SIZE_T bytes = (payload.size() + 1) * sizeof(wchar_t);
    void* address = VirtualAllocEx(process.value, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!address) return 20;
    SIZE_T written = 0;
    if (!WriteProcessMemory(process.value, address, payload.c_str(), bytes, &written) || written != bytes) {
        VirtualFreeEx(process.value, address, 0, MEM_RELEASE); return 21;
    }
    Handle thread(CreateRemoteThread(process.value, nullptr, 0, remoteLoad, address, 0, nullptr));
    if (!thread.value) { VirtualFreeEx(process.value, address, 0, MEM_RELEASE); return 22; }
    const auto wait = WaitForSingleObject(thread.value, 10000);
    if (wait != WAIT_OBJECT_0) return 23;
    VirtualFreeEx(process.value, address, 0, MEM_RELEASE);
    if (!RemoteModule(pid, L"WinDock.Motion.x64.dll")) return 24;
    return 0;
}
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) { return Run(); }
