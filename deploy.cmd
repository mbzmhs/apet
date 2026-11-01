@echo off
setlocal
rem ============================================================
rem APet deploy script: build web + dotnet, publish to the deploy
rem directory, then launch from there.
rem Deploy dir defaults to "dist\" under the repository root, so a plain
rem double-click of this script just works on any machine; override with
rem APET_DEPLOY_DIR to publish elsewhere.
rem User data does NOT live here:
rem   %%APPDATA%%\APet        (setting.json + agents\ - user assets)
rem   %%LOCALAPPDATA%%\APet   (logs, webview2 cache)
rem   Override with APET_DATA_DIR / APET_CACHE_DIR if needed.
rem Notes:
rem   - RuntimeIdentifier is pinned to win-x64 in the csproj, so the
rem     managed output lands in bin\Debug\net8.0-windows\win-x64\
rem   - Native DLLs (gsvtts/onnxruntime/CUDA/cuDNN) are published
rem     explicitly from apet-ui\native\gsv\ - not relied on build copy
rem   - The final "start" uses /D for the working dir and redirects
rem     output to nul: otherwise the launched exe inherits this
rem     script's stdout pipe handle, which keeps the pipe open forever
rem     when invoked as "script | filter"
rem   - ASCII only + CRLF on purpose: cmd.exe parses neither UTF-8
rem     Chinese nor LF-only line endings reliably (kept CRLF via
rem     *.cmd eol=crlf in .gitattributes)
rem ============================================================
set "ROOT=%~dp0"
rem Release + self-contained publish: the .NET runtime ships inside dist,
rem so the folder runs on any x64 Windows 10/11 machine with zero installs.
set "SRC=%ROOT%apet-ui\bin\Release\net8.0-windows\win-x64\publish"
set "NATIVE=%ROOT%apet-ui\native\gsv"
if not defined APET_DEPLOY_DIR set "APET_DEPLOY_DIR=%ROOT%dist"
set "DST=%APET_DEPLOY_DIR%"
rem Optional native DLL packs (GSV TTS engine + Live2D Cubism Core), fetched
rem from the GitHub release when no local native\gsv exists. Adjust the URL
rem if the release repo/tag differs. CPU pack = GSV on CPU + Live2D engine;
rem CUDA and cuDNN packs are pure GPU accelerators. Every download is
rem best-effort: a failure only disables the matching feature, never the app.
set "NATIVE_BASE=https://github.com/mbzmhs/apet/releases/download/v0.1.0"

echo [1/7] Stopping running APet.UI ...
taskkill /IM APet.UI.exe /F >nul 2>&1

echo [2/7] Cleaning stale publish output ...
if exist "%SRC%" rmdir /s /q "%SRC%"
if exist "%DST%\runtimes" rmdir /s /q "%DST%\runtimes"

echo [3/7] Building web frontend (web -^> dist) ...
pushd "%ROOT%web"
call npm install --no-audit --no-fund --fetch-retries=5 --maxsockets=3
if errorlevel 1 (
  echo npm install failed, deploy aborted.
  popd
  exit /b 1
)
call npm run build
if errorlevel 1 (
  echo web build failed, deploy aborted.
  popd
  exit /b 1
)
popd

rem wwwroot is pure build output (hashed Vite assets): clear both sides before the dotnet build
rem (which re-copies fresh dist into SRC\wwwroot) and before sync; otherwise stale bundles linger forever
if exist "%SRC%\wwwroot" rmdir /s /q "%SRC%\wwwroot"
if exist "%DST%\wwwroot" rmdir /s /q "%DST%\wwwroot"

echo [4/7] Publishing apet-ui (Release, self-contained) ...
dotnet publish "%ROOT%apet-ui\apet-ui.csproj" -c Release -p:SelfContained=true --nologo -v q
if errorlevel 1 (
  echo publish failed, deploy aborted.
  exit /b 1
)

echo [5/7] Publishing managed output to %DST% ...
robocopy "%SRC%" "%DST%" /E /XD agents logs /NFL /NDL /NJH /NJS
if errorlevel 8 (
  echo robocopy failed, exit code %errorlevel%.
  exit /b 1
)

echo [6/7] Optional native DLLs (GSV engine, Live2D Cubism Core) ...
if exist "%NATIVE%" (
  robocopy "%NATIVE%" "%DST%" /NFL /NDL /NJH /NJS
  if errorlevel 8 (
    echo native DLL publish failed, exit code %errorlevel%.
    exit /b 1
  )
  goto launch
)
call :fetch_native APet-native-cpu-0.1.0.zip   gsvtts.dll
call :fetch_native APet-native-cuda-0.1.0.zip  cublas64_12.dll
call :fetch_native APet-native-cudnn-0.1.0.zip cudnn64_9.dll
call :fetch_native APet-gsv-models-0.1.0.zip   "models\manifest.json"
goto launch

:fetch_native
rem %~1 = pack file name on the release; %~2 = sentinel DLL, already in
rem %DST% means this pack is deployed and can be skipped
if exist "%DST%\%~2" exit /b 0
echo   downloading %~1 ...
curl.exe -L -f --retry 2 -o "%TEMP%\%~1" "%NATIVE_BASE%/%~1"
if errorlevel 1 (
  echo   download failed - skipped. The matching feature falls back
  echo   ^(GSV on CPU or unavailable, Live2D engine unavailable^); the app is fine.
  exit /b 0
)
"%SystemRoot%\System32\tar.exe" -xf "%TEMP%\%~1" -C "%DST%"
if errorlevel 1 (
  echo   extraction failed - skipped.
  exit /b 0
)
del "%TEMP%\%~1" >nul 2>&1
exit /b 0

:launch

echo [7/7] Deploy done, launching %DST%\APet.UI.exe
start "" /D "%DST%" "APet.UI.exe" >nul 2>&1
