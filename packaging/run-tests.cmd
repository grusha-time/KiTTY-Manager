@echo off
setlocal
cd /d "%~dp0"
if not exist "TestResults" mkdir "TestResults"

if not exist "KiTTYManager.SelfTest.exe" (
  echo ERROR: KiTTYManager.SelfTest.exe not found next to this script.
  exit /b 2
)

set "RUN_DIR=%CD%\TestResults\.current-%RANDOM%-%RANDOM%"
mkdir "%RUN_DIR%"

echo Running offline tests and safe diagnostics...
echo No real SSH logins, subnet scans or all-pairs scans will be performed.
echo.

KiTTYManager.SelfTest.exe --diagnostics --root "%CD%" --output "%RUN_DIR%"
set "RESULT=%ERRORLEVEL%"

set "KMP_RUN_DIR=%RUN_DIR%"
set "KMP_RESULTS_DIR=%CD%\TestResults"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; $source=$env:KMP_RUN_DIR; $destination=$env:KMP_RESULTS_DIR; $report=Join-Path $destination ('kitty-manager-tests-{0}.txt' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')); $files=[IO.Directory]::GetFiles($source,'*.txt'); if($files.Count -eq 0){ throw 'No partial test reports were created.' }; [Array]::Sort($files,[StringComparer]::OrdinalIgnoreCase); $lines=[Collections.Generic.List[string]]::new(); $lines.Add('KiTTY Manager complete test report'); $lines.Add(('Created UTC: {0}' -f [DateTimeOffset]::UtcNow.ToString('O'))); $lines.Add('Contains offline summary and safe diagnostics.'); foreach($file in $files){ $lines.Add(''); $lines.Add(('===== {0} =====' -f [IO.Path]::GetFileNameWithoutExtension($file))); $lines.AddRange([string[]][IO.File]::ReadAllLines($file)) }; [IO.File]::WriteAllLines($report,$lines,[Text.UTF8Encoding]::new($false)); Remove-Item -LiteralPath $source -Recurse -Force; Write-Host ('Complete report: {0}' -f $report)"
if errorlevel 1 (
  echo ERROR: Could not create the combined report. Partial reports remain in:
  echo %RUN_DIR%
  if "%RESULT%"=="0" set "RESULT=3"
)

echo.
if "%RESULT%"=="0" (
  echo All tests completed successfully.
) else (
  echo Tests completed with failures. Exit code: %RESULT%
)
echo Send the newest kitty-manager-tests file from TestResults.
pause
exit /b %RESULT%
