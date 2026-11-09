// Shared between the simulator's board and its driver.
#pragma once
#include <stdint.h>
#include "config.h"

extern uint32_t g_now;                    // the simulated nowMs()
extern uint8_t g_fb[LCD_W * LCD_H * 3];   // what's on the glass, RGB
extern bool g_lifted;
extern bool g_usb;
extern bool g_quiet;
