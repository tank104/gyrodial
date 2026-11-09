# Gyrodial firmware for the Waveshare ESP32-S3-Touch-AMOLED-1.75C, on ESP-IDF v5.5.
#
#   idf.py build
#   idf.py -p COM5 flash
cmake_minimum_required(VERSION 3.16)

# One version for the firmware and the Windows app, kept in ../VERSION (release tags are v<VERSION>).
file(STRINGS "${CMAKE_CURRENT_LIST_DIR}/../VERSION" PROJECT_VER LIMIT_COUNT 1)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${CMAKE_CURRENT_LIST_DIR}/../VERSION")

include($ENV{IDF_PATH}/tools/cmake/project.cmake)

# lv_conf.h and gyrodial_clock.h live in include/, and LVGL (a managed component) needs them too.
idf_build_set_property(COMPILE_OPTIONS "-I${CMAKE_CURRENT_LIST_DIR}/include" APPEND)
idf_build_set_property(COMPILE_DEFINITIONS "LV_CONF_INCLUDE_SIMPLE" APPEND)
idf_build_set_property(COMPILE_DEFINITIONS "LV_LVGL_H_INCLUDE_SIMPLE" APPEND)
# cJSON parses recursively, one level per [ or {, and its default limit is 1000 levels. Every
# protocol message is one flat object, and any BLE central in range can send a line, so a line of
# [[[[... must not be able to run the main task out of stack.
idf_build_set_property(COMPILE_DEFINITIONS "CJSON_NESTING_LIMIT=8" APPEND)

project(gyrodial)
