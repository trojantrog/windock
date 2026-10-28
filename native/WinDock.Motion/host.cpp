#include "protocol.h"
#include "login_task.h"
#include <tlhelp32.h>
#include <cwchar>
#ifndef WIN_DOCK_STARTUP_ONLY
#include "login_host_payload.h"
#endif

namespace {
using motion::Handle;

class Activation {
    Handle mapping;
    motion::ActivationReport local{};
    motion::ActivationReport* report = &local;
public:
    ~Activation() { if (report != &local) UnmapViewOfFile(report); }
    bool Open(DWORD session, const wchar_t* nonce) {
        if (wcslen(nonce) != 32) return false;
        for (const wchar_t* p = nonce; *p; ++p)
            if (!((*p >= L'0' && *p <= L'9') || (*p >= L'a' && *p <= L'f'))) return false;
        wchar_t name[160];
        swprintf_s(name, L"%ls%lu.%ls", motion::ActivationPrefix, session, nonce);
        mapping.Reset(OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name));
        if (!mapping.value) return false;
        auto* view = static_cast<motion::ActivationReport*>(MapViewOfFile(mapping.value, FILE_MAP_READ | FILE_MAP_WRITE,
            0, 0, sizeof(motion::ActivationReport)));
        if (!view) return false;
        report = view;
        ZeroMemory(report, sizeof(*report));
        report->magic = motion::Magic; report->version = motion::Protocol; report->bytes = sizeof(*report);
        report->helper = GetCurrentProcessId(); report->build = motion::Build; report->session = session;
        report->started = GetTickCount64(); report->result = -1;
        return true;
    }
    void Stage(LONG value) { InterlockedExchange(&report->stage, value); }
    void Compositor(DWORD pid) { report->compositor = pid; }
    void RemoteResult(DWORD value) { report->remoteResult = value; }
    int Finish(int result, DWORD error = ERROR_SUCCESS) {
        report->win32 = error; report->finished = GetTickCount64();
        InterlockedExchange(&report->result, result);
        return result;
    }
    int Failed(int result) { return Finish(result, GetLastError()); }
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
    SetLastError(ERROR_NOT_FOUND); return 0;
}

uintptr_t RemoteModule(DWORD pid, const wchar_t* name) {
    Handle snapshot;
    for (unsigned retry = 0; retry < 4; ++retry) {
        snapshot.Reset(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
        if (snapshot.value != INVALID_HANDLE_VALUE) break;
        if (GetLastError() != ERROR_BAD_LENGTH) return 0;
    }
    if (snapshot.value == INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W entry{sizeof(entry)};
    for (BOOL found = Module32FirstW(snapshot.value, &entry); found; found = Module32NextW(snapshot.value, &entry))
        if (_wcsicmp(entry.szModule, name) == 0) return reinterpret_cast<uintptr_t>(entry.modBaseAddr);
    SetLastError(ERROR_MOD_NOT_FOUND); return 0;
}

bool ExistingReady(DWORD session, DWORD pid) {
    wchar_t name[128];
    swprintf_s(name, L"%ls%lu", motion::StatusPrefix, session);
    Handle mapping(OpenFileMappingW(FILE_MAP_READ, FALSE, name));
    if (!mapping.value) return false;
    auto status = static_cast<const motion::Status*>(MapViewOfFile(mapping.value, FILE_MAP_READ, 0, 0, sizeof(motion::Status)));
    if (!status) return false;
    const auto heartbeat = static_cast<uint64_t>(status->heartbeat);
    const auto now = GetTickCount64();
    const bool ready = status->magic == motion::Magic && status->version == motion::Protocol
        && status->bytes == sizeof(*status) && status->pid == pid && status->build == motion::Build
        && status->state == 1 && now >= heartbeat && now - heartbeat < 1500;
    UnmapViewOfFile(status);
    return ready;
}

bool RequestReady(DWORD session, const std::wstring& user) {
    wchar_t mappingName[128];
    swprintf_s(mappingName, L"%ls%lu", motion::ConfigPrefix, session);
    Handle mapping(OpenFileMappingW(FILE_MAP_READ, FALSE, mappingName));
    if (!mapping.value) return false;
    auto config = static_cast<const motion::Config*>(MapViewOfFile(mapping.value, FILE_MAP_READ, 0, 0, sizeof(motion::Config)));
    if (!config) return false;
    motion::Config snapshot{};
    const LONG before = config->sequence;
    MemoryBarrier();
    memcpy(&snapshot, config, sizeof(snapshot));
    MemoryBarrier();
    const LONG after = config->sequence;
    UnmapViewOfFile(config);
    const auto now = GetTickCount64();
    if ((before & 1) || before != after || snapshot.magic != motion::Magic || snapshot.version != motion::Protocol
        || snapshot.bytes != sizeof(snapshot) || !(snapshot.flags & motion::Enabled) || snapshot.publisher <= 4
        || snapshot.count > motion::Capacity || now < snapshot.heartbeat || now - snapshot.heartbeat >= 2000) return false;
    DWORD publisherSession = 0;
    if (!ProcessIdToSessionId(snapshot.publisher, &publisherSession) || publisherSession != session) return false;
    Handle publisher(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, snapshot.publisher));
    if (!publisher.value) return false;
    FILETIME creation{}, exit{}, kernel{}, cpu{};
    if (!GetProcessTimes(publisher.value, &creation, &exit, &kernel, &cpu)) return false;
    const uint64_t started = (static_cast<uint64_t>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime;
    std::wstring publisherUser;
    return started == snapshot.publisherStarted && motion::UserSid(publisher.value, publisherUser) && publisherUser == user;
}

bool PackageSource(Handle& payload, std::wstring& own) {
    wchar_t ownPath[32768];
    const auto ownLength = GetModuleFileNameW(nullptr, ownPath, 32768);
    if (!ownLength || ownLength >= 32768) return false;
    own = ownPath;
    std::wstring directory(ownPath);
    const auto slash = directory.find_last_of(L"\\/");
    if (slash == std::wstring::npos) { SetLastError(ERROR_BAD_PATHNAME); return false; }
    directory.resize(slash + 1);
    const auto source = directory + L"WinDock.Motion.x64.dll";
    payload.Reset(CreateFileW(source.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (payload.value == INVALID_HANDLE_VALUE) return false;
    char digest[65];
    if (!motion::HashFile(payload.value, digest) || strcmp(digest, WIN_DOCK_MOTION_PAYLOAD_SHA256) != 0) {
        SetLastError(ERROR_INVALID_DATA); return false;
    }
    return true;
}

bool SupportedSystem() {
    wchar_t system[32768];
    if (!GetSystemDirectoryW(system, 32768)) return false;
    const auto path = std::wstring(system) + L"\\udwm.dll";
    Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr));
    if (file.value == INVALID_HANDLE_VALUE) return false;
    char digest[65];
    if (!motion::HashFile(file.value, digest) || strcmp(digest, motion::SupportedImageHash) != 0) {
        SetLastError(ERROR_NOT_SUPPORTED); return false;
    }
    return true;
}

#ifndef WIN_DOCK_STARTUP_ONLY
int RegisterLogin(const wchar_t* sid, bool remove) {
    if (!motion::ElevatedFor(sid)) return 40;
    if (remove) return motion::LoginTask(sid, nullptr) == S_OK ? 0 : 44;
    if (!SupportedSystem()) return 41;
    Handle payload;
    std::wstring own;
    if (!PackageSource(payload, own)) return 14;
    motion::PayloadCache cache;
    if (!cache.Prepare(payload.value) || !cache.PrepareHost(motion::LoginHostBytes,
        sizeof(motion::LoginHostBytes), motion::LoginHostSha256)) return 42;
    return motion::LoginTask(sid, &cache.hostPath) == S_OK ? 0 : 43;
}
#endif

int Run(const wchar_t* nonce, const std::wstring& user) {
    DWORD session = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session) || session == 0) return 10;
    Activation activation;
    if (nonce && !activation.Open(session, nonce)) return 10;
    activation.Stage(12);
    if (!RequestReady(session, user)) return activation.Finish(12);
    activation.Stage(14);
    Handle file;
    std::wstring ownPath;
    if (!PackageSource(file, ownPath)) return activation.Failed(14);
    if (!SupportedSystem()) return activation.Finish(41, GetLastError());
    activation.Stage(15);
    if (!EnableDebugPrivilege()) return activation.Failed(15);
    activation.Stage(16);
    const DWORD pid = FindDwm(session);
    if (!pid) return activation.Failed(16);
    activation.Compositor(pid);
    activation.Stage(17);
    Handle process(OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid));
    if (!process.value) return activation.Failed(17);
    activation.Stage(18);
    wchar_t processPath[32768]; DWORD processLength = 32768;
    if (!QueryFullProcessImageNameW(process.value, 0, processPath, &processLength)) return activation.Failed(18);
    wchar_t system[32768];
    if (!GetSystemDirectoryW(system, 32768)) return activation.Failed(18);
    if (_wcsicmp(processPath, (std::wstring(system) + L"\\dwm.exe").c_str()) != 0)
        return activation.Finish(18, ERROR_INVALID_DATA);
    activation.Stage(25);
    if (RemoteModule(pid, L"WinDock.Motion.x64.dll"))
        return activation.Finish(ExistingReady(session, pid) ? 0 : 25);
    if (GetLastError() != ERROR_MOD_NOT_FOUND) return activation.Failed(19);
    activation.Stage(26);
    motion::PayloadCache cache;
    if (!cache.Prepare(file.value)) return activation.Failed(26);
    const auto& payload = cache.path;
    activation.Stage(19);
    auto load = reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
    HMODULE localModule = nullptr;
    if (!load || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(load), &localModule)) return activation.Failed(19);
    wchar_t modulePath[32768];
    if (!GetModuleFileNameW(localModule, modulePath, 32768)) return activation.Failed(19);
    const wchar_t* moduleName = wcsrchr(modulePath, L'\\');
    if (!moduleName) return activation.Finish(19, ERROR_BAD_PATHNAME);
    const auto remoteBase = RemoteModule(pid, moduleName + 1);
    if (!remoteBase) return activation.Failed(19);
    const auto remoteLoad = reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteBase + reinterpret_cast<uintptr_t>(load) - reinterpret_cast<uintptr_t>(localModule));
    activation.Stage(20);
    const SIZE_T bytes = (payload.size() + 1) * sizeof(wchar_t);
    void* address = VirtualAllocEx(process.value, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!address) return activation.Failed(20);
    activation.Stage(21);
    SIZE_T written = 0;
    const BOOL copied = WriteProcessMemory(process.value, address, payload.c_str(), bytes, &written);
    if (!copied || written != bytes) {
        const auto error = !copied ? GetLastError() : ERROR_PARTIAL_COPY;
        VirtualFreeEx(process.value, address, 0, MEM_RELEASE); return activation.Finish(21, error);
    }
    activation.Stage(22);
    Handle thread(CreateRemoteThread(process.value, nullptr, 0, remoteLoad, address, 0, nullptr));
    if (!thread.value) {
        const auto error = GetLastError();
        VirtualFreeEx(process.value, address, 0, MEM_RELEASE); return activation.Finish(22, error);
    }
    activation.Stage(23);
    const auto wait = WaitForSingleObject(thread.value, 10000);
    if (wait != WAIT_OBJECT_0) return activation.Finish(23, wait == WAIT_TIMEOUT ? WAIT_TIMEOUT : GetLastError());
    DWORD remoteResult = 0;
    if (GetExitCodeThread(thread.value, &remoteResult)) activation.RemoteResult(remoteResult);
    VirtualFreeEx(process.value, address, 0, MEM_RELEASE);
    activation.Stage(24);
    if (!RemoteModule(pid, L"WinDock.Motion.x64.dll")) {
        const auto error = GetLastError();
        return activation.Finish(24, error == ERROR_MOD_NOT_FOUND ? ERROR_SUCCESS : error);
    }
    return activation.Finish(0);
}

#ifdef WIN_DOCK_STARTUP_ONLY
int Login(const wchar_t* sid) {
    if (!motion::ElevatedFor(sid)) return 40;
    if (!SupportedSystem()) return 41;
    Handle payload;
    std::wstring own;
    if (!PackageSource(payload, own)) return 14;
    motion::PayloadCache cache;
    if (!cache.Prepare(payload.value) || !cache.VerifyHost(own)) return 42;
    DWORD session = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session) || !session) return 10;
    // Login tasks can run before Windows processes the user's ordinary Run entry.
    // A bounded wait never starts Dock or executes a path supplied by its settings.
    const auto until = GetTickCount64() + 180000;
    while (GetTickCount64() < until) {
        if (RequestReady(session, sid)) return Run(nullptr, sid);
        Sleep(500);
    }
    return 12;
}
#endif
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int count = 0;
    auto args = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!args) return 10;
    int result = 10;
#ifdef WIN_DOCK_STARTUP_ONLY
    if (count == 3 && !wcscmp(args[1], L"--login")) result = Login(args[2]);
#else
    if (count == 3 && !wcscmp(args[1], L"--register-login")) result = RegisterLogin(args[2], false);
    else if (count == 3 && !wcscmp(args[1], L"--remove-login")) result = RegisterLogin(args[2], true);
    else if (count == 2) {
        std::wstring user;
        if (motion::UserSid(GetCurrentProcess(), user) && motion::ElevatedFor(user.c_str())) result = Run(args[1], user);
        else result = 40;
    }
#endif
    LocalFree(args);
    return result;
}
