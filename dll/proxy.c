/*
 * C&C FPS Unlocker - drop-in DLL (d3d9.dll for Red Alert 3, dinput8.dll for Tiberium Wars / Kane's Wrath)
 * Copyright (C) 2026 TheeHorse. GPL v3 or later, see LICENSE.
 *
 * The game loads this instead of the Windows DLL of the same name. It forwards the one function
 * the game uses to the real DLL, and when it's loaded it hooks the game's entry point: just
 * before the game's own startup code runs, it starts .NET inside the game and calls
 * DllEntry.Run(folder) in CnCFpsUnlocker.dll, which applies the same in-memory patches as the
 * Steam launcher, then lets the game start normally. Nothing on disk is changed.
 *
 * Build (Tiny C Compiler): tcc -shared -DPROXY_D3D9 -o d3d9.dll proxy.c
 *                          tcc -shared -DPROXY_DINPUT8 -o dinput8.dll proxy.c
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* Every export of the real DLL is passed through, not only the one the game uses: other tools in
   the game (Tacitus, the C&C:Online patcher, hooks Direct3D) look up the rest by name.
   Each stub drops its own stack frame and jumps to the real function, so arguments, calling
   convention and return value are untouched. */
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
    GetSystemDirectoryW(path, MAX_PATH);   /* SysWOW64 for this 32-bit process */
    lstrcatW(path, REAL_DLL);
    real_dll = LoadLibraryW(path);
    if (!real_dll) return;
#define RESOLVE(name) real_##name = (void *)GetProcAddress(real_dll, #name);
    EXPORTS(RESOLVE)
}

/* --- hosting .NET 4 (mscoree) ------------------------------------------------------------ */
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

/* Called from the entry-point hook, before the game's startup code. */
static void __cdecl on_entry(void)
{
    DWORD old;
    int i;
    if (entry_ptr == entry)
    {
        /* Plain hook: put the entry back. (Not done in the chained case below, where other tools
           like Tacitus may have hooked the entry after us - restoring would undo their hook.) */
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

        /* We're loaded as an import, before the exe's entry point runs, and under the loader
           lock (no .NET here). Redirect the entry point to a small stub instead:
           pushad / call on_entry / popad / [call <first call>] / jmp [entry_ptr]
           All three games start with the MSVC `call __security_init_cookie`; the stub runs that
           call itself and continues after it, so the entry bytes never need putting back. */
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
