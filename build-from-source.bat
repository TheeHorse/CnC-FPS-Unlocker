@rem Builds CnCFpsUnlocker.dll (the fixes, src\Unlocker.cs) with the C# compiler that comes with Windows, no Visual Studio needed.
@rem The tiny d3d9.dll / dinput8.dll are built from dll\proxy.c with dll\build.bat (needs 32-bit MinGW GCC).
@rem The setup is installer\setup.iss, build it with Inno Setup: ISCC.exe /DFiles=<folder with the 3 dlls> installer\setup.iss
@set CSC="%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /platform:x86 /optimize /r:System.Core.dll
@%CSC% /target:library "/out:%~dp0CnCFpsUnlocker.dll" "%~dp0src\Unlocker.cs"
@if errorlevel 1 (echo Build failed. & pause & exit /b 1)
@echo Built CnCFpsUnlocker.dll
@pause
