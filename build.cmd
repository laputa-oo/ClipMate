@echo off
REM ============================================================
REM  ClipMate 一键发布脚本
REM  用法：在项目根目录双击 build.cmd
REM  产物：
REM    publish\portable\ClipMate.exe   （单文件便携版）
REM    publish\installer\ClipMate-Setup.exe （安装包，需已装 Inno Setup）
REM ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo [1/4] 检查 .NET SDK ...
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo   [错误] 未找到 dotnet，请先安装 .NET 8 SDK。
    echo   下载：https://dotnet.microsoft.com/download/dotnet/8.0
    pause & exit /b 1
)

echo [2/4] 还原依赖 + 编译 ...
dotnet publish src\ClipMate\ClipMate.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\portable
if errorlevel 1 ( echo   [错误] 编译失败 & pause & exit /b 1 )

echo [3/4] 便携版完成：publish\portable\ClipMate.exe

echo [4/4] 制作安装包 ...
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" (
    "%ISCC%" packaging\ClipMate.iss
    if not errorlevel 1 echo   安装包完成：publish\installer\ClipMate-Setup.exe
) else (
    echo   [提示] 未找到 Inno Setup 编译器 iscc.exe，跳过安装包。
    echo   安装包制作命令（安装 Inno Setup 后手动执行）：
    echo   iscc.exe packaging\ClipMate.iss
)

echo.
echo 全部完成。
pause
