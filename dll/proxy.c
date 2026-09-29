/* C&C FPS Unlocker drop-in dll - TheeHorse 2026, GPL v3
 * d3d9.dll for ra3, dinput8.dll for tw/kw. forwards to the real dll, hooks the game's entry
 * point and runs CnCFpsUnlocker.dll (same patches as the launcher) before the game starts.
 *
 * tcc -shared -DPROXY_D3D9 -o d3d9.dll proxy.c
 * tcc -shared -DPROXY_DINPUT8 -o dinput8.dll proxy.c
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* forward everything, tacitus looks up the other d3d9 exports by name and crashes otherwise */
#ifdef PROXY_D3D9
#define REAL_DLL L"\\d3d9.dll"
#define EXPORTS(X) X(D3DPERF_BeginEvent) X(D3DPERF_EndEvent) X(D3DPERF_GetStatus) X(D3DPERF_QueryRepeatFrame) \
    X(D3DPERF_SetMarker) X(D3DPERF_SetOptions) X(D3DPERF_SetRegion) X(DebugSetLevel) X(DebugSetMute) \
    X(Direct3D9EnableMaximizedWindowedModeShim) X(Direct3DCreate9) X(Direct3DCreate9Ex) X(Direct3DCreate9On12) \
    X(Direct3DCreate9On12Ex) X(Direct3DShaderValidatorCreate9) X(PSGPError) X(PSGPSampleTexture)
#else
#define REAL_DLL L"\\dinput8.dll"
#define EXPORTS(X) X(DirectInput8Create) X(DllCanUnloadNow) X(DllGetClassObject) X(DllRegisterServer) \
    X(DllUnregisterServer) X(GetdfDIJoystick)
#endif

#define PTR(name) void *real_##name;
EXPORTS(PTR)
#define STUB(name) __declspec(dllexport) void name(void) \
    { __asm__("movl %ebp, %esp\n popl %ebp\n jmp *real_" #name); }
EXPORTS(STUB)

static HMODULE real_dll;
static WCHAR dll_dir[MAX_PATH];
static BYTE *entry, saved[5];
static void *entry_ptr;

static void load_real(void)
{
    WCHAR path[MAX_PATH];
    GetSystemDirectoryW(path, MAX_PATH);   /* redirected to syswow64 */
    lstrcatW(path, REAL_DLL);
    real_dll = LoadLibraryW(path);
    if (!real_dll) return;
#define RESOLVE(name) real_##name = (void *)GetProcAddress(real_dll, #name);
    EXPORTS(RESOLVE)
}

/* --- .net 4 hosting --- */
typedef struct { void **vt; } Com;
typedef HRESULT(__stdcall *CreateInstanceFn)(const GUID *, const GUID *, void **);
typedef HRESULT(__stdcall *GetRuntimeFn)(Com *, LPCWSTR, const GUID *, void **);
typedef HRESULT(__stdcall *GetInterfaceFn)(Com *, const GUID *, const GUID *, void **);
typedef HRESULT(__stdcall *StartFn)(Com *);
typedef HRESULT(__stdcall *ExecFn)(Com *, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, DWORD *);

static const GUID CLSID_CLRMetaHost = {0x9280188d, 0x0e8e, 0x4867, {0xb3, 0x0c, 0x7f, 0xa8, 0x38, 0x84, 0xe8, 0xde}};
static const GUID IID_ICLRMetaHost = {0xd332db9e, 0xb9b3, 0x4125, {0x82, 0x07, 0xa1, 0x48, 0x84, 0xf5, 0x32, 0x16}};
static const GUID IID_ICLRRuntimeInfo = {0xbd39d1d2, 0xba2f, 0x486a, {0x89, 0xb0, 0xb4, 0xb0, 0xcb, 0x46, 0x68, 0x91}};
static const GUID CLSID_CLRRuntimeHost = {0x90f1a06e, 0x7712, 0x4762, {0x86, 0xb5, 0x7a, 0x5e, 0xba, 0x6b, 0xdb, 0x02}};
static const GUID IID_ICLRRuntimeHost = {0x90f1a06c, 0x7712, 0x4762, {0x86, 0xb5, 0x7a, 0x5e, 0xba, 0x6b, 0xdb, 0x02}};

static void run_patches(void)
{
    WCHAR assembly[MAX_PATH];
    HMODULE mscoree;
    CreateInstanceFn create;
    Com *meta = 0, *info = 0, *host = 0;
    DWORD ret = 0;

    lstrcpyW(assembly, dll_dir);
    lstrcatW(assembly, L"\\CnCFpsUnlocker.dll");
    if (GetFileAttributesW(assembly) == INVALID_FILE_ATTRIBUTES) return;
    mscoree = LoadLibraryW(L"mscoree.dll");
    if (!mscoree) return;
    create = (CreateInstanceFn)GetProcAddress(mscoree, "CLRCreateInstance");
    if (!create || create(&CLSID_CLRMetaHost, &IID_ICLRMetaHost, (void **)&meta) < 0) return;
    if (((GetRuntimeFn)meta->vt[3])(meta, L"v4.0.30319", &IID_ICLRRuntimeInfo, (void **)&info) < 0) return;
    if (((GetInterfaceFn)info->vt[9])(info, &CLSID_CLRRuntimeHost, &IID_ICLRRuntimeHost, (void **)&host) < 0) return;
    if (((StartFn)host->vt[3])(host) < 0) return;
    ((ExecFn)host->vt[11])(host, assembly, L"DllEntry", L"Run", dll_dir, &ret);
}

static void __cdecl on_entry(void)
{
    DWORD old;
    int i;
    if (entry_ptr == entry)
    {
        /* only restore if we didn't chain (someone else might have hooked after us) */
        VirtualProtect(entry, 5, PAGE_EXECUTE_READWRITE, &old);
        for (i = 0; i < 5; i++) entry[i] = saved[i];
        VirtualProtect(entry, 5, old, &old);
        FlushInstructionCache(GetCurrentProcess(), entry, 5);
    }
    run_patches();
}

static int is_game_process(void)
{
    WCHAR exe[MAX_PATH];
    int n = GetModuleFileNameW(0, exe, MAX_PATH);
    /* RA3_1.xx.game, cnc3game.dat, cnc3ep1.dat */
    return n > 5 && (lstrcmpiW(exe + n - 5, L".game") == 0 || lstrcmpiW(exe + n - 4, L".dat") == 0);
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        IMAGE_DOS_HEADER *dos;
        IMAGE_NT_HEADERS *nt;
        BYTE *stub, *p;
        DWORD old;
        int i;

        DisableThreadLibraryCalls(inst);
        GetModuleFileNameW(inst, dll_dir, MAX_PATH);
        for (i = lstrlenW(dll_dir); i > 0 && dll_dir[i] != L'\\'; i--) {}
        dll_dir[i] = 0;
        load_real();
        if (!is_game_process()) return TRUE;

        /* loader lock is held here so no .net yet. hook the entry point:
           pushad / call on_entry / popad / [first call] / jmp [entry_ptr]
           entry starts with call __security_init_cookie in all 3 games, run it from the stub */
        dos = (IMAGE_DOS_HEADER *)GetModuleHandleW(0);
        nt = (IMAGE_NT_HEADERS *)((BYTE *)dos + dos->e_lfanew);
        entry = (BYTE *)dos + nt->OptionalHeader.AddressOfEntryPoint;
        entry_ptr = entry;
        stub = (BYTE *)VirtualAlloc(0, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if (!stub) return TRUE;
        p = stub;
        *p++ = 0x60;                                                    /* pushad */
        *p++ = 0xE8; *(DWORD *)p = (DWORD)((BYTE *)on_entry - (p + 4)); p += 4;   /* call on_entry */
        *p++ = 0x61;                                                    /* popad */
        if (entry[0] == 0xE8)
        {
            BYTE *target = entry + 5 + *(int *)(entry + 1);
            *p++ = 0xE8; *(DWORD *)p = (DWORD)(target - (p + 4)); p += 4;         /* call <first call> */
            entry_ptr = entry + 5;
        }
        *p++ = 0xFF; *p++ = 0x25; *(DWORD *)p = (DWORD)&entry_ptr; p += 4;       /* jmp [entry_ptr] */
        for (i = 0; i < 5; i++) saved[i] = entry[i];
        VirtualProtect(entry, 5, PAGE_EXECUTE_READWRITE, &old);
        entry[0] = 0xE9; *(DWORD *)(entry + 1) = (DWORD)(stub - (entry + 5));   /* jmp stub */
        VirtualProtect(entry, 5, old, &old);
        FlushInstructionCache(GetCurrentProcess(), entry, 5);
    }
    return TRUE;
}
