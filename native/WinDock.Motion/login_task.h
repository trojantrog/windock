#pragma once
#include "payload_cache.h"
#include <taskschd.h>
#include <oleauto.h>
#include <vector>

namespace motion {
inline bool UserSid(HANDLE process, std::wstring& sid) {
    Handle token;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token.value)) return false;
    DWORD bytes = 0;
    GetTokenInformation(token.value, TokenUser, nullptr, 0, &bytes);
    if (!bytes) return false;
    std::vector<BYTE> buffer(bytes);
    if (!GetTokenInformation(token.value, TokenUser, buffer.data(), bytes, &bytes)) return false;
    LPWSTR text = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &text)) return false;
    sid = text; LocalFree(text);
    return true;
}

inline bool ElevatedFor(const wchar_t* expectedSid) {
    std::wstring sid;
    if (!expectedSid || !UserSid(GetCurrentProcess(), sid) || sid != expectedSid) {
        SetLastError(ERROR_ACCESS_DENIED); return false;
    }
    Handle token;
    TOKEN_ELEVATION elevation{}; DWORD bytes = 0;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value)
        || !GetTokenInformation(token.value, TokenElevation, &elevation, sizeof(elevation), &bytes)) return false;
    if (!elevation.TokenIsElevated) { SetLastError(ERROR_ELEVATION_REQUIRED); return false; }
    return true;
}

template<class T> struct Com {
    T* value = nullptr;
    ~Com() { if (value) value->Release(); }
    T** Out() { return &value; }
};
struct Bstr {
    BSTR value;
    explicit Bstr(const std::wstring& text) : value(SysAllocStringLen(text.data(), static_cast<UINT>(text.size()))) {}
    ~Bstr() { SysFreeString(value); }
};
struct ComApartment {
    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~ComApartment() { if (SUCCEEDED(result)) CoUninitialize(); }
};

inline std::wstring Xml(const std::wstring& text) {
    std::wstring result;
    for (const auto c : text) {
        if (c == L'&') result += L"&amp;";
        else if (c == L'<') result += L"&lt;";
        else if (c == L'>') result += L"&gt;";
        else if (c == L'\"') result += L"&quot;";
        else result += c;
    }
    return result;
}

inline HRESULT LoginTask(const wchar_t* sid, const std::wstring* helper) {
    if (!ElevatedFor(sid)) return HRESULT_FROM_WIN32(GetLastError());
    ComApartment apartment;
    if (FAILED(apartment.result)) return apartment.result;
    Com<ITaskService> service;
    auto hr = CoCreateInstance(CLSID_TaskScheduler, nullptr, CLSCTX_INPROC_SERVER, IID_ITaskService,
        reinterpret_cast<void**>(service.Out()));
    if (FAILED(hr)) return hr;
    VARIANT empty{}; VariantInit(&empty);
    hr = service.value->Connect(empty, empty, empty, empty);
    if (FAILED(hr)) return hr;
    Com<ITaskFolder> folder;
    Bstr root(L"\\"), name(std::wstring(L"WinDock.NativeMotion.") + sid);
    if (!root.value || !name.value) return E_OUTOFMEMORY;
    hr = service.value->GetFolder(root.value, folder.Out());
    if (FAILED(hr)) return hr;
    if (!helper) {
        hr = folder.value->DeleteTask(name.value, 0);
        return hr == HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) ? S_OK : hr;
    }
    const std::wstring directory = helper->substr(0, helper->find_last_of(L'\\'));
    const std::wstring xml =
        L"<?xml version=\"1.0\" encoding=\"UTF-16\"?>"
        L"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
        L"<RegistrationInfo><Author>WinDock</Author><Description>WinDock window animations</Description></RegistrationInfo>"
        L"<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Xml(sid) +
        L"</UserId><Delay>PT2S</Delay></LogonTrigger></Triggers>"
        L"<Principals><Principal id=\"User\"><UserId>" + Xml(sid) +
        L"</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
        L"<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>"
        L"<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"
        L"<AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>"
        L"<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden>"
        L"<ExecutionTimeLimit>PT4M</ExecutionTimeLimit><Priority>7</Priority></Settings>"
        L"<Actions Context=\"User\"><Exec><Command>" + Xml(*helper) + L"</Command><Arguments>--login " + Xml(sid) +
        L"</Arguments><WorkingDirectory>" + Xml(directory) + L"</WorkingDirectory></Exec></Actions></Task>";
    // The interactive user may inspect/run this fixed action but may not edit it.
    const std::wstring sddl = L"O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;" + std::wstring(sid) + L")";
    Bstr definition(xml), user(sid), security(sddl);
    if (!definition.value || !user.value || !security.value) return E_OUTOFMEMORY;
    VARIANT userId{}, descriptor{}; VariantInit(&userId); VariantInit(&descriptor);
    userId.vt = VT_BSTR; userId.bstrVal = user.value;
    descriptor.vt = VT_BSTR; descriptor.bstrVal = security.value;
    Com<IRegisteredTask> task;
    return folder.value->RegisterTask(name.value, definition.value,
        TASK_CREATE_OR_UPDATE | TASK_DONT_ADD_PRINCIPAL_ACE, userId, empty, TASK_LOGON_INTERACTIVE_TOKEN,
        descriptor, task.Out());
}
}
