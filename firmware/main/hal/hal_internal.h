// What the pieces of the board layer share with each other (not part of the hal.h API).
// Logging: ESP_LOGx with the tag "hal" goes to the UART0 console, never to the USB link.
#pragma once
#include <stddef.h>
#include <stdint.h>
#include <esp_log.h>
#include "puck_clock.h"
#include "../events.h"
#include "../hal.h"

namespace hal {
namespace detail {

// ---- I2C (hal.cpp): one bus for touch, IMU, PMU and the audio codec, on ESP-IDF's i2c_master
// driver at I2C_HZ. Devices are named by 7-bit address and attached on first use. Each call is one
// transaction (a write, a read, or a write then a repeated-start read) with a 20 ms timeout, and
// returns false on a NACK or timeout. Each call takes the bus lock itself; the input task and the
// main loop share the bus, so hold an I2cGuard around a sequence that must not be split.
void i2cBegin();
void i2cLock();
void i2cUnlock();
struct I2cGuard {
  I2cGuard() { i2cLock(); }
  ~I2cGuard() { i2cUnlock(); }
  I2cGuard(const I2cGuard&) = delete;
  I2cGuard& operator=(const I2cGuard&) = delete;
};
bool i2cWrite(uint8_t addr, const uint8_t* data, size_t len);
bool i2cRead(uint8_t addr, uint8_t* data, size_t len);
bool i2cWriteRead(uint8_t addr, const uint8_t* out, size_t outLen, uint8_t* in, size_t inLen);
bool i2cWriteReg(uint8_t addr, uint8_t reg, uint8_t value);         // [reg, value]
bool i2cReadRegs(uint8_t addr, uint8_t reg, uint8_t* out, size_t n);  // [reg], then n bytes
bool i2cProbe(uint8_t addr);                                          // does anything ACK there?

// ---- display (display.cpp) and its LVGL port (lvgl_port.cpp)
bool lcdBegin();                            // SPI bus, reset, init sequence, black frame, panel on
void lcdSetWindow(int x1, int y1, int x2, int y2);   // inclusive, in LVGL coordinates
// Queue pixels for the window just set (bytes already in panel order). `onDone` runs in the SPI
// ISR once the last byte is out. Returns false if the transfer couldn't be queued.
bool lcdQueuePixels(const void* data, size_t bytes, void (*onDone)(void*), void* arg);
void lcdDrain();                            // wait for queued transfers to finish
void lcdCommand(uint8_t cmd, const uint8_t* params, size_t len);
void lcdSleep(bool sleep);
void lcdBrightness(uint8_t value);          // 0..255
void lvglBegin();
void lvglSetPanelAwake(bool awake);         // while asleep, flushes are dropped (panel RAM is redrawn on wake)
void lvglRedrawNow();                       // invalidate everything and render it synchronously

// ---- input plumbing (input.cpp)
void inputBegin();                          // the queue; call before any post
bool post(Ev type, float value = 0, int16_t x = 0, int16_t y = 0, char dir = 0);  // false: queue full
enum class Src : uint8_t { Touch, Turn, Button, Motion };
// A gesture is starting. Notes the activity; when the display is off it posts a single Wake and
// returns false, and the caller must swallow the rest of that gesture.
bool activityBegin(Src src);
void activityContinue();                    // a gesture in progress: just keeps the screen awake
bool displayIsOff();
void inputDisplayChanged(bool on);         // hal::setDisplayOn tells the input side
float turnScale();                          // sensitivity * (flip ? -1 : 1)
void startInputTask();

// ---- the sources, polled from the input task
bool touchBegin();
void touchPoll(uint32_t nowMs);
void touchModeChanged(InputMode mode);
// A tap may be happening: a finger down for less than a tap's length, or lifted within afterMs
bool tapInProgress(uint32_t nowMs, uint32_t afterMs);
bool motionBegin();
void motionPoll(uint32_t nowUs);
void motionModeChanged(InputMode mode);
float motionHeading();
bool motionLifted();
void buttonsBegin();
void buttonsPoll(uint32_t nowMs);
void buttonEvent(bool longPress);           // from the BOOT pin or the PMU's power key
bool powerBegin();
void powerPoll(uint32_t nowMs);             // battery every ~2 s, power key IRQs every ~50 ms

// ---- diagnostics (hal::diag)
bool touchOk();
bool powerOk();
bool audioOk();
int motionDiag(char* buf, size_t len);      // snprintf-style: chars it wanted to write
uint32_t wakesPosted();
int eventCounts(char* buf, size_t len);     // events posted since boot, by type

// ---- sound (audio.cpp, es8311.cpp)
bool es8311Begin(uint32_t sampleRate, uint32_t mclkHz);
void es8311SetVolume(uint8_t reg32);       // 0x00 = -95.5 dB, 0xBF = 0 dB
bool audioBegin();

}  // namespace detail
}  // namespace hal
