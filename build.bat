@echo off
rem Baut Hometray (Release, x64).
rem Ergebnis: Hometray\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hometray.exe
rem Log: build.log
setlocal
cd /d "%~dp0"
set LOG=%~dp0build.log

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo vswhere nicht gefunden - Visual Studio nicht installiert? > "%LOG%"
  exit /b 1
)

rem MSBuild with the .NET SDK resolver (the WinUI XAML compiler needs it)
set MSBUILD=
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -all -prerelease -products * -sort -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
  if not defined MSBUILD if exist "%%~dpiSdkResolvers\Microsoft.DotNet.MSBuildSdkResolver" set "MSBUILD=%%i"
)
if not defined MSBUILD (
  echo Keine Visual-Studio-Installation mit .NET-Unterstuetzung gefunden > "%LOG%"
  exit /b 1
)
echo MSBuild: %MSBUILD% > "%LOG%"

"%MSBUILD%" Hometray\Hometray.csproj -restore -p:Configuration=Release -p:Platform=x64 -m -nologo -v:minimal >> "%LOG%" 2>&1
set RC=%ERRORLEVEL%
echo EXITCODE %RC% >> "%LOG%"
type "%LOG%"
exit /b %RC%
