cmake_minimum_required(VERSION 3.22)
project(YunsevoReconstruction VERSION 0.1 LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
find_package(OpenCV REQUIRED COMPONENTS core imgproc imgcodecs features2d calib3d)
find_path(VULKAN_INCLUDE_DIR vulkan/vulkan.h REQUIRED)
if(NOT GLSLANG_VALIDATOR)
  find_program(GLSLANG_VALIDATOR glslangValidator REQUIRED)
endif()
add_custom_command(OUTPUT ${CMAKE_CURRENT_BINARY_DIR}/depth.spv
  COMMAND ${GLSLANG_VALIDATOR} -V ${CMAKE_CURRENT_SOURCE_DIR}/depth.comp -o ${CMAKE_CURRENT_BINARY_DIR}/depth.spv
  DEPENDS depth.comp VERBATIM)
find_package(Python3 REQUIRED COMPONENTS Interpreter)
add_custom_command(OUTPUT ${CMAKE_CURRENT_BINARY_DIR}/shader.h
  COMMAND ${Python3_EXECUTABLE} ${CMAKE_CURRENT_SOURCE_DIR}/embed.py ${CMAKE_CURRENT_BINARY_DIR}/depth.spv ${CMAKE_CURRENT_BINARY_DIR}/shader.h
  DEPENDS ${CMAKE_CURRENT_BINARY_DIR}/depth.spv embed.py VERBATIM)
add_library(yunsevo_reconstruction SHARED engine.cpp vulkan.cpp ${CMAKE_CURRENT_BINARY_DIR}/shader.h)
target_include_directories(yunsevo_reconstruction PRIVATE ${CMAKE_CURRENT_BINARY_DIR} ${VULKAN_INCLUDE_DIR})
target_link_libraries(yunsevo_reconstruction PRIVATE ${OpenCV_LIBS} ${CMAKE_DL_LIBS})
if(ANDROID)
  target_link_options(yunsevo_reconstruction PRIVATE -Wl,-z,max-page-size=16384)
endif()
add_executable(yunsevo-reconstruct main.cpp)
target_link_libraries(yunsevo-reconstruct PRIVATE yunsevo_reconstruction)
