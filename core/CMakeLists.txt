cmake_minimum_required(VERSION 3.22)
project(sphoto_core VERSION 0.1.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(APPLE)
  set(_default_gles OFF)
else()
  set(_default_gles ON)
endif()

option(SPHOTO_GLES "Build the OpenGL ES 3 backend" ${_default_gles})
option(SPHOTO_EGL "Build the offscreen EGL context helper" ${_default_gles})
option(SPHOTO_METAL "Build the Metal backend" ${APPLE})
option(SPHOTO_SHARED "Build a shared library" OFF)
option(SPHOTO_TESTS "Build the tests" ${PROJECT_IS_TOP_LEVEL})
option(SPHOTO_CLI "Build the sphoto-cli tool" ${PROJECT_IS_TOP_LEVEL})

set(_sources src/core.cpp src/cpu_pipeline.cpp src/filters.cpp src/geometry.cpp
  src/grading.cpp src/recipe.cpp)
if(SPHOTO_GLES)
  list(APPEND _sources src/gles/gles_renderer.cpp)
endif()
if(SPHOTO_EGL)
  list(APPEND _sources src/gles/egl_context.cpp)
endif()
if(SPHOTO_METAL)
  enable_language(OBJCXX)
  list(APPEND _sources src/metal/metal_renderer.mm)
endif()

if(SPHOTO_SHARED)
  add_library(sphoto_core SHARED ${_sources})
  target_compile_definitions(sphoto_core PRIVATE SPHOTO_BUILD_SHARED)
  # Stays sphoto_core.dll: "sphoto.dll" would clash with the Windows app's
  # own managed SPhoto.dll, since Windows file names ignore case.
else()
  add_library(sphoto_core STATIC ${_sources})
endif()

target_include_directories(sphoto_core PUBLIC include)
# The CPU pipeline splits large images across threads.
find_package(Threads REQUIRED)
target_link_libraries(sphoto_core PRIVATE Threads::Threads)
target_compile_definitions(sphoto_core PRIVATE SPHOTO_VERSION="${PROJECT_VERSION}")
if(MSVC)
  target_compile_options(sphoto_core PRIVATE /W4)
else()
  target_compile_options(sphoto_core PRIVATE -Wall -Wextra)
endif()

# Graphics libraries. Windows uses ANGLE from vcpkg; other platforms use the
# system GLES/EGL. SPHOTO_GL_LIBS overrides the choice.
if(SPHOTO_GLES OR SPHOTO_EGL)
  if(DEFINED SPHOTO_GL_LIBS)
    set(_gl_libs ${SPHOTO_GL_LIBS})
  elseif(WIN32)
    find_package(unofficial-angle CONFIG REQUIRED)
    set(_gl_libs unofficial::angle::libEGL unofficial::angle::libGLESv2)
    # vcpkg's ANGLE targets carry no include directories; the Khronos
    # headers come from its egl-registry and opengl-registry dependencies.
    find_path(SPHOTO_GL_INCLUDE_DIR GLES3/gl3.h REQUIRED)
    target_include_directories(sphoto_core PUBLIC ${SPHOTO_GL_INCLUDE_DIR})
  elseif(ANDROID OR OHOS)
    set(_gl_libs GLESv3 EGL)
  else()
    set(_gl_libs GLESv2 EGL)
  endif()
  target_link_libraries(sphoto_core PUBLIC ${_gl_libs})
endif()

if(SPHOTO_METAL)
  target_compile_options(sphoto_core PRIVATE $<$<COMPILE_LANGUAGE:OBJCXX>:-fobjc-arc>)
  target_link_libraries(sphoto_core PUBLIC "-framework Metal" "-framework Foundation")
endif()

if(SPHOTO_TESTS)
  enable_testing()
  add_executable(sphoto_tests tests/main.cpp tests/cpu_test.cpp tests/gpu_test.cpp
    tests/recipe_test.cpp tests/grading_test.cpp tests/tone_test.cpp)
  target_link_libraries(sphoto_tests PRIVATE sphoto_core)
  target_compile_definitions(sphoto_tests PRIVATE
    SPHOTO_TEST_GLES=$<BOOL:${SPHOTO_EGL}>
    SPHOTO_TEST_METAL=$<BOOL:${SPHOTO_METAL}>)
  add_test(NAME sphoto_tests COMMAND sphoto_tests)
endif()

if(SPHOTO_CLI)
  add_library(sphoto_stb STATIC tools/stb_impl.c)
  target_include_directories(sphoto_stb PUBLIC third_party/stb)
  add_executable(sphoto_cli tools/sphoto_cli.cpp)
  set_target_properties(sphoto_cli PROPERTIES OUTPUT_NAME sphoto-cli)
  target_link_libraries(sphoto_cli PRIVATE sphoto_core sphoto_stb)
  target_compile_definitions(sphoto_cli PRIVATE SPHOTO_CLI_GPU=$<BOOL:${SPHOTO_EGL}>)
endif()
