@echo off
:: One-click build script for the repository root.
:: Runs dotnet restore then builds the solution. Does not modify source code.

setlocal enabledelayedexpansion

echo ==================================================================
echo Running repository setup: restore + build
echo ==================================================================
echo Restoring NuGet packages...
dotnet restore
if %errorlevel% neq0 (
 echo dotnet restore failed.
 pause
 exit /b1
)

echo Building solution (Release)...
dotnet build -c Release
if %errorlevel% neq0 (
 echo dotnet build failed.
 pause
 exit /b1
)

echo Build completed successfully.
echo To run the WPF app from this script, execute:
echo dotnet run --project src\ProgrammerShell.App --configuration Release
echo Or publish a self-contained bundle (Windows x64) using:
echo dotnet publish src\ProgrammerShell.App -c Release -r win-x64 --self-contained true -o publish\win-x64

echo ==================================================================
echo Press any key to exit...
pause >nul
endlocal
