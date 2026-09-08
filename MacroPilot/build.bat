@echo off
setlocal
REM ============================================================
REM MacroPilot 发布脚本（重写版）。输出到【非桌面】目录，绝不动桌面/MacroPilot_dist。
REM   build.bat        -> 自包含（默认）：含 .NET 运行时，目标机零依赖，双击 MacroPilot.exe 即用
REM   build.bat fd     -> 框架依赖（小体积）：目标机需 .NET 8 桌面运行时
REM 输出目录：%USERPROFILE%\MacroPilot_review （可改下面 DIST）
REM 依赖（WPF-UI / System.IO.Ports）走 NuGet 还原（首次需联网）。
REM ============================================================

set "PROJ=%~dp0MacroPilot.csproj"
if not defined DIST set "DIST=%USERPROFILE%\MacroPilot_review"

taskkill /im MacroPilot.exe /f >nul 2>&1
if exist "%DIST%" rmdir /s /q "%DIST%"

if /i "%~1"=="fd" (
    echo == Framework-dependent publish ==
    dotnet publish "%PROJ%" -c Release -p:DebugType=none -p:DebugSymbols=false -o "%DIST%"
) else (
    echo == Self-contained publish ==
    dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -o "%DIST%"
)

if errorlevel 1 ( echo. & echo [FAILED] dotnet publish failed. & exit /b 1 )

echo.
echo [OK] Published -^> %DIST%
echo      Run "%DIST%\MacroPilot.exe"
endlocal
