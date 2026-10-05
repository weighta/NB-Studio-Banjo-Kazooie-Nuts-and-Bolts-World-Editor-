@echo off
rem Builds reNut with NB's mod layer. Run it from a reNut source tree that apply.cmd prepared.
rem
rem   RENUT_SDK   ReXGlue SDK (the win-amd64 folder of rexglue-sdk-0.10.0.x-win-amd64.zip)
rem   RENUT_LLVM  LLVM/Clang 20+ (the folder with bin\clang++.exe)
rem   NB_CLI      NB.Cli.exe (NB Studio / NB Multiplayer command-line tool)
rem   NB_GAME     your untouched game folder (default.xex + Bundle): only read
rem
rem   1. NB.Cli renut-layer: hooks at every NB executable-mod site (config\nb_hooks.toml)
rem   2. codegen through the build system (target renut_codegen, so it is not run again later)
rem   3. NB.Cli renut-layer-apply: the hook calls become the NB interpreter check
rem   4. build: out\build\win-amd64-release\renut.exe (+ rexruntime.dll)
setlocal
set ROOT=%~dp0
for %%v in (RENUT_SDK RENUT_LLVM NB_CLI NB_GAME) do if not defined %%v (echo set %%v first & exit /b 1)
set VS=C:\Program Files\Microsoft Visual Studio\2022\Community
if not exist "%VS%" set VS=C:\Program Files\Microsoft Visual Studio\2022\Professional
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set PATH=%RENUT_LLVM%\bin;%VS%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin;%VS%\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja;%PATH%
cd /d "%ROOT%"
if not exist "%ROOT%assets\default.xex" (mkdir "%ROOT%assets" 2>nul & copy /y "%NB_GAME%\default.xex" "%ROOT%assets\default.xex" >nul)
"%NB_CLI%" renut-layer "%ROOT%." "%ROOT%assets\default.xex" || exit /b 1
rem the first codegen runs before configuring: configure only adds the recompiled code when generated\sources.cmake exists
if not exist "%ROOT%generated\sources.cmake" ("%RENUT_SDK%\bin\rexglue.exe" codegen renut_manifest.toml || exit /b 1)
cmake --preset win-amd64-release -DCMAKE_PREFIX_PATH="%RENUT_SDK%" -DRENUT_ASSETS_DIR="%NB_GAME%" || exit /b 1
cmake --build --preset win-amd64-release --target renut_codegen || exit /b 1
"%NB_CLI%" renut-layer-apply "%ROOT%." || exit /b 1
cmake --build --preset win-amd64-release %*
