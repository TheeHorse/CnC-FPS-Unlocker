@rem Builds RA3HighFps.exe from src\ using the C# compiler that ships with Windows.
@rem No Visual Studio or SDK needed.
@"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /platform:x86 /target:winexe /optimize /r:System.Windows.Forms.dll /r:System.Drawing.dll "/resource:%~dp0src\banner.jpg,banner.jpg" "/out:%~dp0RA3HighFps.exe" "%~dp0src\RA3HighFps.cs"
@if errorlevel 1 (echo Build failed.) else (echo Built RA3HighFps.exe)
@pause
