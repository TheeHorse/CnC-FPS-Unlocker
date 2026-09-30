@rem Builds d3d9.dll (RA3) and dinput8.dll (TW/KW) from proxy.c with 32-bit MinGW GCC (i686).
@rem Pass the path to gcc.exe if it's not on your PATH, e.g.  build.bat C:\mingw32\bin\gcc.exe
@rem No C runtime: the dll only uses kernel32, so it has no dependencies.
@set GCC=%~1
@if "%GCC%"=="" set GCC=gcc
@cd /d "%~dp0"
@set FLAGS=-shared -O2 -s -nostdlib -ffreestanding -fno-asynchronous-unwind-tables -Wl,-e,_DllMain@12 -Wl,--enable-stdcall-fixup proxy.c -lkernel32
@"%GCC%" -DPROXY_D3D9 -o d3d9.dll %FLAGS% || (echo Build failed. & exit /b 1)
@"%GCC%" -DPROXY_DINPUT8 -o dinput8.dll %FLAGS% || (echo Build failed. & exit /b 1)
@echo Built d3d9.dll and dinput8.dll
