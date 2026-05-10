# Exclr8CEF — top-level CMake config.
#
# Locates the CEF binary distribution placed at third_party/cef/<platform>/
# by scripts/download-cef.sh and includes subprojects.

cmake_minimum_required(VERSION 3.21)
project(exclr8cef CXX C)

# CEF 147 requires C++20.
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# ---- Detect platform suffix used by download-cef.sh -----------------------
if(APPLE)
    if(CMAKE_HOST_SYSTEM_PROCESSOR STREQUAL "arm64")
        set(_EX_PLATFORM "macosarm64")
    else()
        set(_EX_PLATFORM "macosx64")
    endif()
elseif(WIN32)
    set(_EX_PLATFORM "windows64")
elseif(UNIX)
    if(CMAKE_HOST_SYSTEM_PROCESSOR MATCHES "aarch64|arm64")
        set(_EX_PLATFORM "linuxarm64")
    else()
        set(_EX_PLATFORM "linux64")
    endif()
endif()

set(EXCLR8CEF_CEF_ROOT
    "${CMAKE_SOURCE_DIR}/../third_party/cef/${_EX_PLATFORM}"
    CACHE PATH "Path to extracted CEF binary distribution")

if(NOT EXISTS "${EXCLR8CEF_CEF_ROOT}/include/cef_version.h")
    message(FATAL_ERROR
        "CEF binary distribution not found at:\n  ${EXCLR8CEF_CEF_ROOT}\n"
        "Run scripts/download-cef.sh first.")
endif()

message(STATUS "Exclr8CEF: using CEF at ${EXCLR8CEF_CEF_ROOT}")

list(APPEND CMAKE_MODULE_PATH "${EXCLR8CEF_CEF_ROOT}/cmake")
set(CEF_ROOT "${EXCLR8CEF_CEF_ROOT}")
find_package(CEF REQUIRED)

add_subdirectory("${CEF_LIBCEF_DLL_WRAPPER_PATH}" libcef_dll_wrapper)

# ---- Subprojects ----------------------------------------------------------
add_subdirectory(shim)
add_subdirectory(demo)
