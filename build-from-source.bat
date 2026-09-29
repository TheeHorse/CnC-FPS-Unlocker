@rem Builds everything from src\ with the C# compiler that comes with Windows, no Visual Studio needed.
@rem   RA3HighFps.exe      - the setup / Steam launcher
@rem   CnCFpsUnlocker.dll  - the same code as a library, used by the drop-in (manual install) version
@rem The tiny d3d9.dll / dinput8.dll from the drop-in zip are built from dll\proxy.c with dll\build.ps1 (needs Tiny C Compiler).
@set CSC="%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /platform:x86 /optimize /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.CSharp.dll "/resource:%~dp0src\banner.jpg,banner.jpg"
@%CSC% /target:winexe "/out:%~dp0RA3HighFps.exe" "%~dp0src\RA3HighFps.cs"
@if errorlevel 1 (echo Build failed. & pause & exit /b 1)
@%CSC% /target:library "/out:%~dp0CnCFpsUnlocker.dll" "%~dp0src\RA3HighFps.cs"
@if errorlevel 1 (echo Build failed. & pause & exit /b 1)
@echo Built RA3HighFps.exe and CnCFpsUnlocker.dll
@pause
