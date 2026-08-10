@echo off
setlocal
set "ROOT=%~dp0"
set "APP=%ROOT%dist\MirrorHid\MirrorHid.App.exe"
set "DOTNET=dotnet"

if exist "%ROOT%.tools\dotnet\dotnet.exe" (
  set "DOTNET=%ROOT%.tools\dotnet\dotnet.exe"
)

if not exist "%APP%" (
  echo Publishing MirrorHid for first use...
  "%DOTNET%" publish "%ROOT%src\MirrorHid.App\MirrorHid.App.csproj" -c Release -r win-x64 --self-contained true -o "%ROOT%dist\MirrorHid"
  if errorlevel 1 exit /b %errorlevel%
)

start "" "%APP%"
