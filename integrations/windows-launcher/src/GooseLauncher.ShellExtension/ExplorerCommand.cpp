#include "ExplorerCommand.h"

#include <sddl.h>
#include <servprov.h>
#include <shlguid.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <cstring>
#include <new>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
#define RETURN_IF_FAILED(expression) do { const HRESULT value = (expression); if (FAILED(value)) return value; } while (false)

namespace
{
    constexpr DWORD MaxFiles = 8;
    constexpr DWORD PipeTimeoutMs = 700;

    bool IsChineseUi() noexcept { return PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_CHINESE; }

    HRESULT CopyString(const wchar_t* source, PWSTR* destination) noexcept
    {
        if (!destination) return E_POINTER;
        *destination = nullptr;
        const auto bytes = (wcslen(source) + 1) * sizeof(wchar_t);
        auto result = static_cast<PWSTR>(CoTaskMemAlloc(bytes));
        if (!result) return E_OUTOFMEMORY;
        std::memcpy(result, source, bytes); *destination = result; return S_OK;
    }

    HRESULT GetFolderFromSite(IUnknown* site, std::wstring& folder)
    {
        if (!site) return E_FAIL;
        ComPtr<IServiceProvider> serviceProvider;
        RETURN_IF_FAILED(site->QueryInterface(IID_PPV_ARGS(&serviceProvider)));
        ComPtr<IFolderView> folderView;
        RETURN_IF_FAILED(serviceProvider->QueryService(SID_SFolderView, IID_PPV_ARGS(&folderView)));
        ComPtr<IShellItem> folderItem;
        RETURN_IF_FAILED(folderView->GetFolder(IID_PPV_ARGS(&folderItem)));
        PWSTR path = nullptr;
        RETURN_IF_FAILED(folderItem->GetDisplayName(SIGDN_FILESYSPATH, &path));
        folder.assign(path); CoTaskMemFree(path); return S_OK;
    }

    HRESULT ResolveActivation(IShellItemArray* items, IUnknown* site, std::wstring& folder, std::vector<std::wstring>& files)
    {
        if (items)
        {
            DWORD count = 0;
            if (SUCCEEDED(items->GetCount(&count)) && count > 0)
            {
                if (count > MaxFiles) return HRESULT_FROM_WIN32(ERROR_TOO_MANY_OPEN_FILES);
                for (DWORD index = 0; index < count; ++index)
                {
                    ComPtr<IShellItem> item; RETURN_IF_FAILED(items->GetItemAt(index, &item));
                    PWSTR rawPath = nullptr; RETURN_IF_FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &rawPath));
                    const std::wstring path(rawPath); CoTaskMemFree(rawPath);
                    const auto attributes = GetFileAttributesW(path.c_str());
                    if (attributes == INVALID_FILE_ATTRIBUTES) return HRESULT_FROM_WIN32(GetLastError());
                    if ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        if (count != 1) return HRESULT_FROM_WIN32(ERROR_DIRECTORY);
                        folder = path; return S_OK;
                    }
                    const auto separator = path.find_last_of(L"\\/");
                    if (separator == std::wstring::npos) return E_FAIL;
                    const auto parent = path.substr(0, separator);
                    if (folder.empty()) folder = parent;
                    else if (_wcsicmp(folder.c_str(), parent.c_str()) != 0) return HRESULT_FROM_WIN32(ERROR_NOT_SAME_DEVICE);
                    files.push_back(path);
                }
                return S_OK;
            }
        }
        return GetFolderFromSite(site, folder);
    }

    std::wstring CurrentUserPipeName()
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return {};
        DWORD bytes = 0; GetTokenInformation(token, TokenUser, nullptr, 0, &bytes);
        std::vector<BYTE> buffer(bytes);
        if (!GetTokenInformation(token, TokenUser, buffer.data(), bytes, &bytes)) { CloseHandle(token); return {}; }
        CloseHandle(token);
        PWSTR sidText = nullptr;
        if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &sidText)) return {};
        std::wstring sid(sidText); LocalFree(sidText);
        for (auto& ch : sid) if (ch == L'-') ch = L'_';
        return L"\\\\.\\pipe\\GooseLauncher.Activation." + sid;
    }

    std::wstring JsonEscape(const std::wstring& value)
    {
        std::wstring escaped; escaped.reserve(value.size() + 8);
        for (const auto ch : value)
        {
            switch (ch) { case L'\\': escaped += L"\\\\"; break; case L'\"': escaped += L"\\\""; break; case L'\r': escaped += L"\\r"; break; case L'\n': escaped += L"\\n"; break; case L'\t': escaped += L"\\t"; break; default: escaped += ch; }
        }
        return escaped;
    }

    std::string Utf8(const std::wstring& value)
    {
        const auto size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
        if (size <= 0) return {};
        std::string result(static_cast<size_t>(size), '\0');
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr); return result;
    }

    std::wstring SerializeFiles(const std::vector<std::wstring>& files)
    {
        std::wstring json = L"[";
        for (size_t index = 0; index < files.size(); ++index) { if (index) json += L","; json += L"\"" + JsonEscape(files[index]) + L"\""; }
        return json + L"]";
    }

    bool SendWarmActivation(const std::wstring& folder, const std::vector<std::wstring>& files, const POINT cursor)
    {
        const auto pipeName = CurrentUserPipeName(); if (pipeName.empty()) return false;
        const auto filesJson = files.empty() ? std::wstring{} : L",\"files\":" + SerializeFiles(files);
        const auto json = L"{\"protocolVersion\":1,\"type\":\"show_prompt\",\"folder\":\"" + JsonEscape(folder) + L"\"" + filesJson + L",\"x\":" + std::to_wstring(cursor.x) + L",\"y\":" + std::to_wstring(cursor.y) + L"}";
        const auto payload = Utf8(json);
        if (payload.empty() || payload.size() > 32 * 1024) return false;
        std::vector<BYTE> framed(sizeof(DWORD) + payload.size());
        const auto length = static_cast<DWORD>(payload.size());
        std::memcpy(framed.data(), &length, sizeof(length)); std::memcpy(framed.data() + sizeof(length), payload.data(), payload.size());
        BYTE ack = 0; DWORD read = 0;
        return CallNamedPipeW(pipeName.c_str(), framed.data(), static_cast<DWORD>(framed.size()), &ack, sizeof(ack), &read, PipeTimeoutMs) && read == 1 && ack == 1;
    }

    bool StartLauncherHost()
    {
        wchar_t path[MAX_PATH]{};
        if (!g_moduleInstance || !GetModuleFileNameW(g_moduleInstance, path, ARRAYSIZE(path))) return false;
        if (!PathRemoveFileSpecW(path) || !PathAppendW(path, L"GooseLauncher.exe")) return false;
        if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) return false;
        return reinterpret_cast<INT_PTR>(ShellExecuteW(
            nullptr, L"open", path, L"--tray", nullptr, SW_SHOWNORMAL)) > 32;
    }

    void ActivateCold(const std::wstring& folder, const std::vector<std::wstring>& files, const POINT cursor)
    {
        if (!StartLauncherHost()) return;
        for (int attempt = 0; attempt < 30; ++attempt)
        {
            Sleep(100);
            if (SendWarmActivation(folder, files, cursor)) return;
        }
    }
}

ExplorerCommand::ExplorerCommand() noexcept { ++g_objectCount; }
ExplorerCommand::~ExplorerCommand() { --g_objectCount; }
HRESULT ExplorerCommand::QueryInterface(REFIID iid, void** object) noexcept
{
    if (!object) return E_POINTER; *object = nullptr;
    if (iid == IID_IUnknown || iid == IID_IExplorerCommand) *object = static_cast<IExplorerCommand*>(this);
    else if (iid == IID_IObjectWithSite) *object = static_cast<IObjectWithSite*>(this); else return E_NOINTERFACE;
    AddRef(); return S_OK;
}
ULONG ExplorerCommand::AddRef() noexcept { return ++referenceCount_; }
ULONG ExplorerCommand::Release() noexcept { const auto count = --referenceCount_; if (!count) delete this; return count; }
HRESULT ExplorerCommand::GetTitle(IShellItemArray*, PWSTR* name) noexcept { CaptureInvocationOrigin(); return CopyString(IsChineseUi() ? L"使用 Goose" : L"Ask Goose", name); }
HRESULT ExplorerCommand::GetIcon(IShellItemArray*, PWSTR* icon) noexcept
{
    wchar_t path[MAX_PATH]{};
    if (!g_moduleInstance || !GetModuleFileNameW(g_moduleInstance, path, ARRAYSIZE(path))) return E_FAIL;
    if (!PathRemoveFileSpecW(path) || !PathAppendW(path, L"..\\Assets\\Goose.ico")) return E_FAIL;
    return GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES ? HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) : CopyString(path, icon);
}
HRESULT ExplorerCommand::GetToolTip(IShellItemArray*, PWSTR* infoTip) noexcept { return CopyString(IsChineseUi() ? L"在此位置打开 Goose" : L"Open Goose here", infoTip); }
HRESULT ExplorerCommand::GetCanonicalName(GUID* guid) noexcept { if (!guid) return E_POINTER; *guid = CLSID_GooseLauncherExplorerCommand; return S_OK; }
HRESULT ExplorerCommand::GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) noexcept { CaptureInvocationOrigin(); if (!state) return E_POINTER; *state = ECS_ENABLED; return S_OK; }
HRESULT ExplorerCommand::GetFlags(EXPCMDFLAGS* flags) noexcept { if (!flags) return E_POINTER; *flags = ECF_DEFAULT; return S_OK; }
HRESULT ExplorerCommand::EnumSubCommands(IEnumExplorerCommand**) noexcept { return E_NOTIMPL; }
HRESULT ExplorerCommand::Invoke(IShellItemArray* items, IBindCtx*) noexcept
{
    try
    {
        std::wstring folder; std::vector<std::wstring> files;
        const auto result = ResolveActivation(items, site_.Get(), folder, files); if (FAILED(result)) return result;
        const auto cursor = InvocationOrigin(); if (!SendWarmActivation(folder, files, cursor)) ActivateCold(folder, files, cursor); return S_OK;
    }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; } catch (...) { return E_FAIL; }
}
void ExplorerCommand::CaptureInvocationOrigin() noexcept
{
    AcquireSRWLockExclusive(&originLock_); if (!hasInvocationOrigin_) hasInvocationOrigin_ = GetCursorPos(&invocationOrigin_) != FALSE; ReleaseSRWLockExclusive(&originLock_);
}
POINT ExplorerCommand::InvocationOrigin() noexcept
{
    CaptureInvocationOrigin(); AcquireSRWLockShared(&originLock_); const auto origin = invocationOrigin_; ReleaseSRWLockShared(&originLock_); return origin;
}
HRESULT ExplorerCommand::SetSite(IUnknown* site) noexcept { site_ = site; return S_OK; }
HRESULT ExplorerCommand::GetSite(REFIID iid, void** site) noexcept { if (!site) return E_POINTER; *site = nullptr; return site_ ? site_->QueryInterface(iid, site) : E_FAIL; }
