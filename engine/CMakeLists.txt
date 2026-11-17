cmake_minimum_required(VERSION 3.20)
project(UfoEngine LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(MSVC)
  add_compile_options(/W4 /permissive- /fp:precise)
endif()

# Portable signal processing: no Windows headers, so it is tested on its own.
add_library(ufo_dsp STATIC
  dsp/biquad.cpp
  dsp/limiter.cpp
  dsp/engine.cpp
  dsp/voice.cpp)
target_include_directories(ufo_dsp PUBLIC dsp)

add_executable(ufo_dsp_tests tests/main.cpp)
target_link_libraries(ufo_dsp_tests PRIVATE ufo_dsp)
