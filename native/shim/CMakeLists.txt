# Exclr8CEF shim — shared library exposing a C ABI on top of CEF.
#
# Layout:
#   exclr8cef                  - shared library, the C ABI surface
#   exclr8cef_version_probe    - Stage 1 smoke test (no CEF init)

set(SHIM_LIB exclr8cef)
set(SHIM_TEST exclr8cef_version_probe)

set(SHIM_SRCS
    exclr8cef.h
    exclr8cef.cc
    exclr8cef_app.h
    exclr8cef_app.cc
    exclr8cef_client.h
    exclr8cef_client.cc
    exclr8cef_osr.h
    exclr8cef_osr.cc
)
if(OS_MAC)
    list(APPEND SHIM_SRCS exclr8cef_mac.mm)
endif()

# ---- Shared library: the C ABI surface ------------------------------------
add_library(${SHIM_LIB} SHARED ${SHIM_SRCS})
SET_LIBRARY_TARGET_PROPERTIES(${SHIM_LIB})
add_dependencies(${SHIM_LIB} libcef_dll_wrapper)

target_include_directories(${SHIM_LIB}
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}
    PRIVATE "${CEF_ROOT}"
)

target_compile_definitions(${SHIM_LIB} PRIVATE EXCLR8CEF_BUILDING)

set_target_properties(${SHIM_LIB} PROPERTIES
    C_VISIBILITY_PRESET hidden
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
)

if(OS_MAC)
    set_source_files_properties(exclr8cef_mac.mm PROPERTIES
        COMPILE_FLAGS "-fobjc-arc"
    )
    set_target_properties(${SHIM_LIB} PROPERTIES
        MACOSX_RPATH ON
        INSTALL_NAME_DIR "@rpath"
    )
endif()

# On macOS the CEF framework is loaded at runtime via CefScopedLibraryLoader,
# so we link only libcef_dll_wrapper — NOT libcef_lib.
if(OS_MAC)
    target_link_libraries(${SHIM_LIB} PRIVATE
        libcef_dll_wrapper
        ${CEF_STANDARD_LIBS}
    )
else()
    target_link_libraries(${SHIM_LIB} PRIVATE
        libcef_lib
        libcef_dll_wrapper
        ${CEF_STANDARD_LIBS}
    )
endif()

# ---- Stage 1 smoke test ---------------------------------------------------
add_executable(${SHIM_TEST} version_probe.c)
target_link_libraries(${SHIM_TEST} PRIVATE ${SHIM_LIB})

if(OS_MAC)
    set_target_properties(${SHIM_TEST} PROPERTIES
        INSTALL_RPATH "@executable_path"
        BUILD_WITH_INSTALL_RPATH TRUE
    )
endif()

add_custom_command(TARGET ${SHIM_TEST} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:${SHIM_LIB}>
        $<TARGET_FILE_DIR:${SHIM_TEST}>
)
