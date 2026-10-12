cmake_minimum_required(VERSION 3.5.0)
project(sphoto_harmony LANGUAGES CXX)

set(SPHOTO_EGL OFF CACHE BOOL "" FORCE)
set(SPHOTO_TESTS OFF CACHE BOOL "" FORCE)
set(SPHOTO_CLI OFF CACHE BOOL "" FORCE)
add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/../../../../../core sphoto_core)

add_library(sphoto SHARED napi_init.cpp)
target_link_libraries(sphoto PRIVATE
  sphoto_core
  ace_ndk.z ace_napi.z hilog_ndk.z EGL GLESv3)
