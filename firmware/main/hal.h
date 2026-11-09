// The board: display (with the LVGL port), touch, motion, buttons, power and sound.
// Implemented in src/hal/. Everything here is called from the main loop unless noted.
#pragma once
#include <stdint.h>
#include "events.h"

enum class Tone : uint8_t {
  Tick,     // a detent: very short, quiet click
  Tap,      // a tap landed
  Select,   // a face picked / an option chosen
  Success,  // rising two notes
  Error,    // low buzz
  Chime,    // the focus timer is done: a short gentle melody
};

namespace hal {

// Power, I2C, display + LVGL (lv_init, draw buffers, flush, 2-px rounder), touch, IMU, buttons,
// audio, and the input task (pinned to core 0) that samples touch/IMU/buttons/PMU.
void begin();

// ---- display
void setBrightness(uint8_t percent);    // 0..100, applied immediately
void setDisplayOn(bool on);             // panel sleep / wake (LVGL keeps running)
bool displayOn();

// ---- input
bool pollEvent(InputEvent& out);        // non-blocking; pops the next event for the faces
void setInputMode(InputMode mode);      // faces call this when the trackpad face is entered/left
InputMode inputMode();
float heading();                        // total degrees turned since boot (clockwise +), drift-corrected
bool lifted();                          // true when the dial is held up (screen not facing up)
uint32_t lastActivityMs();              // nowMs() of the last touch, turn, button or motion
void setTurnSensitivity(float k);       // multiplies Turn values (from cfg.sens)
void setTurnFlip(bool flip);            // reverses Turn direction (from cfg.flip)

// ---- power (values refreshed by the input task every ~2 s; reads are cheap)
int batteryPercent();                   // 0..100, or -1 when no battery is attached
int batteryMv();
bool charging();
bool usbPowered();
void powerOff();                        // AXP2101 shutdown (does nothing useful on USB power)

// ---- sound (ES8311 + speaker amp); non-blocking, plays from a small audio task
void beep(Tone t);
void setSoundEnabled(bool on);

// ---- misc
uint64_t chipId();                      // from the eFuse MAC
// One line of live state for debugging on a real board (which chips answered, raw motion,
// display/brightness, activity): hostlink answers {"t":"diag"} with it.
void diag(char* buf, size_t len);
}  // namespace hal
