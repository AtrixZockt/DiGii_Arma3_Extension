@echo off
REM Build script for digii_file Arma 3 extension
REM Produces native DLLs via .NET NativeAOT for both 32-bit and 64-bit

REM Add vswhere to PATH (needed by NativeAOT linker discovery)
set "PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer"

REM ── 64-bit build ──────────────────────────────────────────────────
echo Setting up MSVC 64-bit environment...
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1

echo Building 64-bit DLL...
dotnet publish "%~dp0digii_file.csproj" -r win-x64 -c Release

if %ERRORLEVEL% NEQ 0 (
    echo 64-bit build FAILED!
    exit /b 1
)
echo 64-bit build successful!
echo.

REM ── 32-bit build ──────────────────────────────────────────────────
echo Setting up MSVC 32-bit environment...
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars32.bat" >nul 2>&1

echo Building 32-bit DLL...
dotnet publish "%~dp0digii_file.csproj" -r win-x86 -c Release

if %ERRORLEVEL% NEQ 0 (
    echo 32-bit build FAILED!
    exit /b 1
)
echo 32-bit build successful!
echo.

REM ── Patch x86 exports ──────────────────────────────────────────
REM Arma 3 Publisher requires stdcall-decorated names (_RVExtension@12)
REM for 32-bit DLLs. NativeAOT/MSVC always strip stdcall decoration,
REM so we patch the PE export table directly after building.
echo Patching 32-bit DLL with stdcall-decorated exports...
python "%~dp0patch_exports.py" "%~dp0bin\x86\Release\net10.0\win-x86\publish\digii_file.dll"

if %ERRORLEVEL% NEQ 0 (
    echo Export patching FAILED!
    exit /b 1
)
echo.

REM ── Copy DLLs to project root ────────────────────────────────────
echo Copying DLLs to project root...
copy /Y "%~dp0bin\x64\Release\net10.0\win-x64\publish\digii_file.dll" "%~dp0..\..\digii_file_x64.dll"
copy /Y "%~dp0bin\x86\Release\net10.0\win-x86\publish\digii_file.dll" "%~dp0..\..\digii_file.dll"

echo.
echo Both builds successful! DLLs copied to project root:
echo   digii_file_x64.dll (64-bit)
echo   digii_file.dll     (32-bit)
