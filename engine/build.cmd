@echo off
rem Builds the M274 with the Visual Studio 2022 Build Tools and runs its tests.
rem Output goes to engine\build; the app picks UfoApo.dll up from there.
setlocal
call "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul 2>nul
if not exist "%~dp0build" mkdir "%~dp0build"
cd /d "%~dp0build"

set DSP=..\dsp\biquad.cpp ..\dsp\limiter.cpp ..\dsp\engine.cpp ..\dsp\voice.cpp
set FLAGS=/nologo /std:c++20 /O2 /W4 /EHsc /permissive- /MT /DUNICODE /D_UNICODE /DNOMINMAX /I ..\dsp /I ..\apo

cl %FLAGS% ..\tests\main.cpp %DSP% /Fe:ufo_dsp_tests.exe || exit /b 1
cl %FLAGS% /LD ..\apo\UfoApo.cpp %DSP% /Fe:UfoApo.dll /link /DEF:..\apo\UfoApo.def AudioBaseProcessingObjectV140.lib audiomediatypecrt.lib legacy_stdio_definitions.lib ole32.lib uuid.lib advapi32.lib || exit /b 1
cl %FLAGS% ..\tests\apo_harness.cpp /Fe:apo_harness.exe /link audiomediatypecrt.lib ole32.lib uuid.lib || exit /b 1

.\ufo_dsp_tests.exe || exit /b 1
.\apo_harness.exe "%~dp0build\UfoApo.dll" || exit /b 1
