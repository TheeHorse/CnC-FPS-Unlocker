# Builds d3d9.dll (RA3) and dinput8.dll (TW/KW) with the Tiny C Compiler.
param([string]$Tcc = "G:\tools\tcc\tcc.exe")
Set-Location $PSScriptRoot
& $Tcc -shared -DPROXY_D3D9 -o d3d9.dll proxy.c
if ($LASTEXITCODE -ne 0) { throw "tcc failed for d3d9.dll" }
& $Tcc -shared -DPROXY_DINPUT8 -o dinput8.dll proxy.c
if ($LASTEXITCODE -ne 0) { throw "tcc failed for dinput8.dll" }
Remove-Item d3d9.def, dinput8.def -ErrorAction SilentlyContinue
"built d3d9.dll, dinput8.dll"
