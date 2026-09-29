@rem Builds everything from src\ with the C# compiler that comes with Windows, no Visual Studio needed.
@rem   CnCFpsUnlocker.dll  - the patch code as a library, used by the drop-in (manual install) version
@rem   RA3HighFps.exe      - the setup / Steam launcher (carries CnCFpsUnlocker.dll inside for non-Steam installs)
@rem The tiny d3d9.dll / dinput8.dll are built from dll\proxy.c with dll\build.bat (needs Tiny C Compiler).
@rem If they're in dll\ they get packed into the exe too, otherwise non-Steam installs from the setup are skipped.
@set CSC="%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /platform:x86 /optimize /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.CSharp.dll "/resource:%~dp0src\banner.jpg,banner.jpg"
@%CSC% /target:library "/out:%~dp0CnCFpsUnlocker.dll" "%~dp0src\RA3HighFps.cs"
@if errorlevel 1 (echo Build failed. & pause & exit /b 1)
@set RES="/resource:%~dp0CnCFpsUnlocker.dll,CnCFpsUnlocker.dll"
@if exist "%~dp0dll\d3d9.dll" set RES=%RES% "/resource:%~dp0dll\d3d9.dll,d3d9.dll"
@if exist "%~dp0dll\dinput8.dll" set RES=%RES% "/resource:%~dp0dll\dinput8.dll,dinput8.dll"
@%CSC% /target:winexe %RES% "/out:%~dp0RA3HighFps.exe" "%~dp0src\RA3HighFps.cs"
@if errorlevel 1 (echo Build failed. & pause & exit /b 1)
@echo Built CnCFpsUnlocker.dll and RA3HighFps.exe
@pause
