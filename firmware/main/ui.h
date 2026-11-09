// The faces. Implemented in src/ui/. All of it runs in the main loop (LVGL is not thread-safe).
#pragma once
#include <stdint.h>
#include "events.h"

namespace ui {

void begin();                          // build the faces (hal::begin already set up LVGL)
void handleInput(const InputEvent& e); // one input event from hal
void update(uint32_t dirty);           // g_model changed (Dirty bits)
void tick(uint32_t nowMs);             // every loop, before lv_timer_handler(): animation, timers,
                                       // dimming / screen-off policy
const char* currentFace();             // face id, as in docs/PROTOCOL.md

}  // namespace ui
