@echo off
setlocal
set "ROOT=%~dp0"
set "APP=%ROOT%dist\MirrorHid\MirrorHid.App.exe"
set "DOTNET=dotnet"

if exist "%ROOT%.tools\dotnet\dotnet.exe" (
  set "DOTNET=%ROOT%.tools\dotnet\dotnet.exe"
)

echo Updating MirrorHid from the current source...
"%DOTNET%" publish "%ROOT%src\MirrorHid.App\MirrorHid.App.csproj" -c Release -r win-x64 --self-contained true -o "%ROOT%dist\MirrorHid"
if errorlevel 1 exit /b 1

start "" "%APP%"
