@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo =========================================
echo 正在编译 RiMCP_v2 Launcher 独立版本...
echo =========================================
dotnet publish src\RiMCP.Launcher\RiMCP.Launcher.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./app
if %errorlevel% neq 0 (
    echo.
    echo [错误] 编译失败，请检查是否安装了 .NET 8 SDK，或查看上方错误信息。
    pause
    exit /b
)
echo.
echo [成功] 编译完成！请双击 app\RiMCP.Launcher.exe 启动程序。
pause
