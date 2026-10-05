@echo off
rem Adds NB's mod layer to a reNut source tree:  apply.cmd <reNut source folder>
rem Copies src\nb, applies renut-nb.patch (CMakeLists.txt, renut_app.h, the path wizard) and puts build_renut.cmd there.
setlocal
set KIT=%~dp0
set DST=%~1
if "%DST%"=="" (echo usage: apply.cmd ^<reNut source folder^> & exit /b 1)
if not exist "%DST%\CMakeLists.txt" (echo %DST% is not a reNut source folder & exit /b 1)
xcopy /y /i /q "%KIT%src\nb" "%DST%\src\nb" >nul || exit /b 1
copy /y "%KIT%config\nb_fixes.toml" "%DST%\config\nb_fixes.toml" >nul || exit /b 1
copy /y "%KIT%build_renut.cmd" "%DST%\build_renut.cmd" >nul
rem the SDK boilerplate reNut includes (made by "rexglue init", with reNut's generated\ layout)
if not exist "%DST%\generated\rexglue.cmake" (mkdir "%DST%\generated" 2>nul & copy /y "%KIT%generated\rexglue.cmake" "%DST%\generated\rexglue.cmake" >nul)
where git >nul 2>nul && (git -C "%DST%" apply --whitespace=nowarn "%KIT%renut-nb.patch" && echo patched with git & exit /b 0)
where patch >nul 2>nul && (patch -d "%DST%" -p1 -i "%KIT%renut-nb.patch" && echo patched & exit /b 0)
echo Could not apply renut-nb.patch automatically (needs git or patch): apply it by hand.
exit /b 1
