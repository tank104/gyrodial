// The clock everything runs on: time since boot, and sleeping. C and C++ (LVGL's tick uses it).
// On the board it is esp_timer; the face simulator (tools/sim) provides its own nowMs().
#pragma once
#include <stdint.h>

#ifdef ESP_PLATFORM
#include <esp_timer.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>

static inline uint32_t nowMs(void) { return (uint32_t)(esp_timer_get_time() / 1000); }
static inline uint32_t nowUs(void) { return (uint32_t)esp_timer_get_time(); }
// Sleeps at least one tick, so lower-priority tasks (and the idle task's watchdog) always get a turn.
static inline void sleepMs(uint32_t ms) {
  TickType_t t = pdMS_TO_TICKS(ms);
  vTaskDelay(t ? t : 1);
}
#else
#ifdef __cplusplus
extern "C" {
#endif
uint32_t nowMs(void);
#ifdef __cplusplus
}
#endif
#endif
