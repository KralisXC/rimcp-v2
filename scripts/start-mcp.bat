@echo off
chcp 65001 >nul
setlocal

echo RiMCP_v2 MCP server - manual start (debug)
echo Logs appear in this window.
echo.
pause

dotnet run --project "%~dp0..\src\RiMCP.McpServer"

echo.
echo Server exited.
pause