@echo off
setlocal

echo.
echo ================================
echo  Joe's Scanner - Build Installer
echo ================================
echo.

REM This script is located in the Installer folder.
REM Compute the project root as the parent directory of this folder.
set "INSTALLER_DIR=%~dp0"
set "PROJECT_ROOT=%INSTALLER_DIR%.."

REM Normalize and move to project root
cd /d "%PROJECT_ROOT%"

REM Project file and publish output folder
REM IMPORTANT: publish to Installer\publish-unpackaged so it matches MyAppDir ".\publish-unpackaged"
set "PROJECT=JoesScanner.csproj"
set "PUBLISH_DIR=Installer\publish-unpackaged"

echo Cleaning previous publish output...
if exist "%PUBLISH_DIR%" rd /s /q "%PUBLISH_DIR%"

echo.
echo Publishing self contained Windows x64 build...
dotnet publish "%PROJECT%" ^
  -c Release ^
  -f net10.0-windows10.0.19041.0 ^
  -r win-x64 ^
  -p:WindowsPackageType=None ^
  -p:WindowsAppSDKSelfContained=true ^
  -p:UseMonoRuntime=false ^
  --self-contained true ^
  -o "%PUBLISH_DIR%"

if errorlevel 1 (
  echo.
  echo dotnet publish failed. Check the errors above.
  echo.
  pause
  exit /b 1
)

echo.
echo Publish complete.
echo Output folder:
echo   %CD%\%PUBLISH_DIR%
echo.

echo Building installer with Inno Setup...

REM Change into the Installer folder so the .iss behaves exactly like when run manually
cd /d "%INSTALLER_DIR%"

REM If Inno is installed in Program Files instead of Program Files (x86),
REM change the path on the next line accordingly.
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" "JoesScannerSetup.iss"
set "INNO_ERR=%ERRORLEVEL%"

if not "%INNO_ERR%"=="0" (
  echo.
  echo Inno compilation failed. Check the messages above from ISCC.
  echo.
  pause
  exit /b 1
)

echo.
echo Done. If there were no errors above, your installer should be in:
echo   C:\Users\nate\Desktop\InstallerOutput
echo.
pause
endlocal
