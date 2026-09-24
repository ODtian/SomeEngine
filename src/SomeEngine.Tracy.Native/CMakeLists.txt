cmake_minimum_required(VERSION 3.24)

project(SomeEngineTracyNative LANGUAGES CXX)

option(TRACY_ON_DEMAND "Enable Tracy on-demand capture mode." OFF)
option(TRACY_ONLY_LOCALHOST "Restrict Tracy to localhost connections." ON)

add_library(SomeEngineTracyNative SHARED
    SomeEngineTracyBridge.cpp
    ../../external/tracy/public/TracyClient.cpp
)

target_include_directories(SomeEngineTracyNative
    PRIVATE
        ../../external/tracy/public
)

target_compile_features(SomeEngineTracyNative PRIVATE cxx_std_20)

target_compile_definitions(SomeEngineTracyNative
    PRIVATE
        TRACY_ENABLE
        TRACY_DELAYED_INIT
        TRACY_MANUAL_LIFETIME
        WIN32_LEAN_AND_MEAN
        NOMINMAX
)

if(TRACY_ON_DEMAND)
    target_compile_definitions(SomeEngineTracyNative PRIVATE TRACY_ON_DEMAND)
endif()

if(TRACY_ONLY_LOCALHOST)
    target_compile_definitions(SomeEngineTracyNative PRIVATE TRACY_ONLY_LOCALHOST)
endif()

if(MSVC)
    target_compile_options(SomeEngineTracyNative PRIVATE /utf-8 /EHsc)
endif()

target_link_libraries(SomeEngineTracyNative PRIVATE ws2_32 dbghelp)

set_target_properties(SomeEngineTracyNative PROPERTIES
    OUTPUT_NAME "SomeEngineTracy.Native"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts/$<CONFIG>"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts/$<CONFIG>"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts/$<CONFIG>"
    PDB_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts/$<CONFIG>"
    COMPILE_PDB_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/artifacts/$<CONFIG>"
)
