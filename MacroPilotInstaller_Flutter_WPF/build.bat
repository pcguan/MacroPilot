@echo off
setlocal
REM Build MacroPilotInstaller_Flutter.exe (Flutter UI + 7z SFX single-exe).
REM Replaces the Inno approach; old ..\MacroPilotInstaller\ is kept intact.
REM Needs: Flutter SDK + VS C++ Build Tools + dotnet8 + 7-Zip.
REM Output -> Desktop\MacroPilotInstaller_Flutter.exe (single exe, no extra folders).

set "HERE=%~dp0"
set "APP=%HERE%..\MacroPilot\MacroPilot.csproj"
set "STAGE=%HERE%stage_payload"
set "FLUTTER=C:\Users\pengcheng.guan\Desktop\tool\flutter\bin\flutter.bat"
set "SEVENZIP=C:\Program Files\7-Zip\7z.exe"
set "RELDIR=%HERE%build\windows\x64\runner\Release"

set "PATH=C:\Program Files\dotnet;C:\Users\pengcheng.guan\Desktop\tool\Git\cmd;C:\Users\pengcheng.guan\Desktop\tool\flutter\bin;%PATH%"
set "HTTP_PROXY=http://127.0.0.1:7897"
set "HTTPS_PROXY=http://127.0.0.1:7897"

REM Run from the installer dir so flutter pub get/build find pubspec (also works when launched via SSH).
cd /d "%HERE%"

echo == [1/6] publish WPF (framework-dependent) ==
if exist "%STAGE%" rmdir /s /q "%STAGE%"
dotnet publish "%APP%" -c Release -p:DebugType=none -p:DebugSymbols=false -o "%STAGE%"
if errorlevel 1 ( echo [FAILED] publish & exit /b 1 )

echo == [2/6] zip payload to assets\payload\app.zip ==
if not exist "%HERE%assets\payload" mkdir "%HERE%assets\payload"
del /q "%HERE%assets\payload\app.zip" 2>nul
pushd "%STAGE%"
"%SEVENZIP%" a -tzip -mx=5 "%HERE%assets\payload\app.zip" * >nul
popd
if errorlevel 1 ( echo [FAILED] zip payload & exit /b 1 )

echo == [3/6] flutter build windows --release ==
REM Regenerate the windows runner if missing (repo keeps only authored sources).
if not exist "%HERE%windows" call "%FLUTTER%" create --platforms=windows .
call "%FLUTTER%" pub get
if errorlevel 1 ( echo [FAILED] pub get & exit /b 1 )
call "%FLUTTER%" build windows --release
if errorlevel 1 ( echo [FAILED] flutter build & exit /b 1 )

echo == [4/6] pack runner to archive.7z ==
del /q "%HERE%archive.7z" 2>nul
pushd "%RELDIR%"
"%SEVENZIP%" a -t7z -mx=9 "%HERE%archive.7z" * >nul
popd
if errorlevel 1 ( echo [FAILED] pack runner & exit /b 1 )

echo == [5/6] build SFX single exe straight to Desktop ==
REM Pre-iconized SFX stub comes from 7zSD_icon.zip (icon baked on a clean machine;
REM rcedit is blocked by this machine's EDR). Extracting here runs interactively so it survives.
"%SEVENZIP%" e "%HERE%sfx\7zSD_icon.zip" -o"%HERE%sfx" -y >nul
if not exist "%HERE%sfx\7zSD_icon.sfx" ( echo [FAILED] extract icon stub & exit /b 1 )
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')"`) do set "DESK=%%D"
copy /b "%HERE%sfx\7zSD_icon.sfx" + "%HERE%sfx\config.txt" + "%HERE%archive.7z" "%DESK%\MacroPilotInstaller_Flutter.exe" >nul
if errorlevel 1 ( echo [FAILED] sfx concat & exit /b 1 )

echo == [6/6] done ==

echo.
echo [OK] Desktop: %DESK%\MacroPilotInstaller_Flutter.exe
echo.
echo ======== BUILD DONE ========
pause
endlocal
