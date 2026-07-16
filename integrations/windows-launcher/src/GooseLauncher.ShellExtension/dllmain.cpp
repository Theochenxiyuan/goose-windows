#include "ExplorerCommand.h"
#include <new>

std::atomic<long> g_objectCount{ 0 };
HINSTANCE g_moduleInstance = nullptr;

class ClassFactory final : public IClassFactory
{
public:
    ClassFactory() noexcept { ++g_objectCount; }
    ~ClassFactory() { --g_objectCount; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) noexcept override
    {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (iid != IID_IUnknown && iid != IID_IClassFactory) return E_NOINTERFACE;
        *object = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override { return ++referenceCount_; }
    ULONG STDMETHODCALLTYPE Release() noexcept override { const auto count = --referenceCount_; if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** object) noexcept override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto command = new (std::nothrow) ExplorerCommand();
        if (!command) return E_OUTOFMEMORY;
        const auto result = command->QueryInterface(iid, object); command->Release(); return result;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) noexcept override { lock ? ++g_objectCount : --g_objectCount; return S_OK; }
private:
    std::atomic<ULONG> referenceCount_{ 1 };
};

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH) { g_moduleInstance = instance; DisableThreadLibraryCalls(instance); }
    return TRUE;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return g_objectCount.load() == 0 ? S_OK : S_FALSE; }
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** object)
{
    if (clsid != CLSID_GooseLauncherExplorerCommand) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) ClassFactory();
    if (!factory) return E_OUTOFMEMORY;
    const auto result = factory->QueryInterface(iid, object); factory->Release(); return result;
}

