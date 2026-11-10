@echo off
rem Baut das MSIX-Paket von Hometray fuer den Microsoft Store.
rem
rem   build-msix.bat [x64^|ARM64] [store^|test]
rem
rem   store (Standard): unsigniertes .msix fuer Partner Center (der Store signiert es)
rem   test            : mit einem selbstsignierten Zertifikat signiertes .msix zum lokalen Installieren
rem
rem Ergebnis: AppPackages\   Log: build-msix.log
setlocal
cd /d "%~dp0"
set LOG=%~dp0build-msix.log
set PLATFORM=%~1
if "%PLATFORM%"=="" set PLATFORM=x64
set MODE=%~2
if "%MODE%"=="" set MODE=store

echo ==== MSIX %PLATFORM% %MODE% ==== > "%LOG%"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo vswhere nicht gefunden - Visual Studio nicht installiert? >> "%LOG%"
  echo EXITCODE 9001 >> "%LOG%"
  type "%LOG%"
  exit /b 1
)

rem same MSBuild selection as build.bat (the WinUI XAML compiler needs the .NET SDK resolver)
set MSBUILD=
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -all -prerelease -products * -sort -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
  if not defined MSBUILD if exist "%%~dpiSdkResolvers\Microsoft.DotNet.MSBuildSdkResolver" set "MSBUILD=%%i"
)
if not defined MSBUILD (
  echo Keine Visual-Studio-Installation mit .NET-Unterstuetzung gefunden >> "%LOG%"
  echo EXITCODE 9003 >> "%LOG%"
  type "%LOG%"
  exit /b 1
)
echo MSBuild: %MSBUILD% >> "%LOG%"

rem separate bin/obj folders, so the MSIX build does not mix with the normal (unpackaged) build
set PKGARGS=-p:Packaged=true -p:BaseOutputPath=bin-msix\ -p:BaseIntermediateOutputPath=obj-msix\ -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never "-p:AppxPackageDir=%~dp0AppPackages\\"

if /i "%MODE%"=="test" goto test
rem (.msixupload is only produced by the UWP pipeline; Partner Center accepts the .msix)
set PKGARGS=%PKGARGS% -p:AppxPackageSigningEnabled=false
goto build

:test
rem Self-signed certificate matching the Publisher in Package.appxmanifest (msix-test-cert.ps1)
set THUMB=
for /f "usebackq tokens=*" %%t in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0msix-test-cert.ps1"`) do set THUMB=%%t
if not defined THUMB (
  echo Testzertifikat konnte nicht erstellt werden >> "%LOG%"
  echo EXITCODE 9004 >> "%LOG%"
  type "%LOG%"
  exit /b 1
)
echo Testzertifikat: %THUMB% >> "%LOG%"
set PKGARGS=%PKGARGS% -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=%THUMB%

:build
echo ==== Build MSIX (Hometray) ==== >> "%LOG%"
"%MSBUILD%" Hometray\Hometray.csproj -restore -p:Configuration=Release -p:Platform=%PLATFORM% %PKGARGS% -m -nologo -v:minimal >> "%LOG%" 2>&1
set RC=%ERRORLEVEL%

echo ==== Ergebnis ==== >> "%LOG%"
dir /s /b AppPackages\Hometray_*.msix >> "%LOG%" 2>&1
echo EXITCODE %RC% >> "%LOG%"
type "%LOG%"
exit /b %RC%
