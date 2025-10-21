@echo off
chcp 65001
cls

cd ..
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None -p:DebugSymbols=false

robocopy "bin\Release\net8.0\win-x64\publish" "bin\Release" /E
cd bin\Release
rd /s /q net8.0

pause
