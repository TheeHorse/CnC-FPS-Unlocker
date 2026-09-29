@rem Builds d3d9.dll (RA3) and dinput8.dll (TW/KW) from proxy.c with Tiny C Compiler.
@rem Pass the path to tcc.exe if it's not on your PATH, e.g.  build.bat C:\tcc\tcc.exe
@set TCC=%~1
@if "%TCC%"=="" set TCC=tcc
@cd /d "%~dp0"
@"%TCC%" -shared -DPROXY_D3D9 -o d3d9.dll proxy.c || (echo Build failed. & exit /b 1)
@"%TCC%" -shared -DPROXY_DINPUT8 -o dinput8.dll proxy.c || (echo Build failed. & exit /b 1)
@del /q d3d9.def dinput8.def 2>nul
@echo Built d3d9.dll and dinput8.dll
