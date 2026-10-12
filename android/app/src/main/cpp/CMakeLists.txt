cmake_minimum_required(VERSION 3.22)
project(sphoto_android LANGUAGES CXX)

set(SPHOTO_EGL OFF CACHE BOOL "" FORCE)
set(SPHOTO_TESTS OFF CACHE BOOL "" FORCE)
set(SPHOTO_CLI OFF CACHE BOOL "" FORCE)
add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/../../../../../core sphoto_core)

add_library(sphoto_jni SHARED sphoto_jni.cpp)
target_link_libraries(sphoto_jni PRIVATE sphoto_core log)
