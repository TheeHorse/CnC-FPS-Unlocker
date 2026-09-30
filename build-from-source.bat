@rem Builds the mod from src\ with the C# compiler that comes with Windows, no Visual Studio needed.
@rem   CnCFpsUnlocker.dll          - the fixes (src\Unlocker.cs), loaded inside the game
@rem   CnC-FPS-Unlocker-Setup.exe  - the optional installer (src\Setup.cs), just copies files
@rem The tiny d3d9.dll / dinput8.dll are built from dll\proxy.c with dll\build.bat (needs 32-bit MinGW GCC).
@set CSC="%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /platform:x86 /optimize /r:System.Core.dll
@%CSC% /target:library "/out:%~dp0CnCFpsUnlocker.dll" "%~dp0src\Unlocker.cs"
@if errorlevel 1 (echo Build failed. & pause & exit /b 1)
@%CSC% /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll "/resource:%~dp0src\banner.jpg,banner.jpg" "/out:%~dp0CnC-FPS-Unlocker-Setup.exe" "%~dp0src\Setup.cs"
@if errorlevel 1 (echo Build failed. & pause & exit /b 1)
@echo Built CnCFpsUnlocker.dll and CnC-FPS-Unlocker-Setup.exe
@pause
