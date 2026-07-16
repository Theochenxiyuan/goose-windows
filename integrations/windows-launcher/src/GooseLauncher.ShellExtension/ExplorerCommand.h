#pragma once

#include <windows.h>
#include <shobjidl_core.h>
#include <wrl/client.h>
#include <atomic>

// {9F092D2E-33CD-41A5-B898-1E8F6A594A73}
inline constexpr GUID CLSID_GooseLauncherExplorerCommand =
{ 0x9f092d2e, 0x33cd, 0x41a5, { 0xb8, 0x98, 0x1e, 0x8f, 0x6a, 0x59, 0x4a, 0x73 } };

extern std::atomic<long> g_objectCount;
extern HINSTANCE g_moduleInstance;

class ExplorerCommand final : public IExplorerCommand, public IObjectWithSite
{
public:
    ExplorerCommand() noexcept;
    ~ExplorerCommand();
    IFACEMETHODIMP QueryInterface(REFIID iid, void** object) noexcept override;
    IFACEMETHODIMP_(ULONG) AddRef() noexcept override;
    IFACEMETHODIMP_(ULONG) Release() noexcept override;
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) noexcept override;
    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR*) noexcept override;
    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* infoTip) noexcept override;
    IFACEMETHODIMP GetCanonicalName(GUID* guidCommandName) noexcept override;
    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) noexcept override;
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) noexcept override;
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) noexcept override;
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand**) noexcept override;
    IFACEMETHODIMP SetSite(IUnknown* site) noexcept override;
    IFACEMETHODIMP GetSite(REFIID iid, void** site) noexcept override;
private:
    void CaptureInvocationOrigin() noexcept;
    POINT InvocationOrigin() noexcept;
    std::atomic<ULONG> referenceCount_{ 1 };
    Microsoft::WRL::ComPtr<IUnknown> site_;
    SRWLOCK originLock_ = SRWLOCK_INIT;
    POINT invocationOrigin_{};
    bool hasInvocationOrigin_ = false;
};

