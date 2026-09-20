@echo off
setlocal
cd /d "%~dp0"

echo 正在发布 win-x64 自包含单文件版本...
echo.

dotnet publish .\src\LineTester.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish

if errorlevel 1 (
    echo.
    echo 发布失败，请检查上面的错误信息。
    pause
    exit /b 1
)

echo.
echo 发布完成：%~dp0publish\LineTester.exe
echo appsettings.json 已自动复制到同目录。
pause
