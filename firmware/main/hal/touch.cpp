// The glass: CST9217 touch controller, and the gestures read from it. In trackpad mode the glass
// also drives the PC's mouse directly (see InputMode::Trackpad).
#include <math.h>
#include <stdlib.h>
#include <atomic>
#include <driver/gpio.h>
#include "../config.h"
#include "../hostlink.h"
#include "cst9217.h"
#include "hal_internal.h"

namespace hal {
namespace detail {

namespace {

static const char* TAG = "hal";

constexpr int kTapSlopPx = 14;            // more than this and it's a drag, not a tap
constexpr uint32_t kTapMaxMs = 450;          // up to long-press territory: a slow, deliberate tap still counts
constexpr uint32_t kDoubleTapMs = 300;    // between the first release and the second tap
constexpr int kDoubleTapSlopPx = 40;
constexpr uint32_t kLongPressMs = 600;
constexpr uint32_t kSwipeMaxMs = 400;
constexpr int kSwipeMinPx = 60;
constexpr uint32_t kMoveEventMs = 16;     // TouchMove events to the faces at most ~60 Hz
constexpr float kTwoFingerScrollPx = 30;  // trackpad: two-finger drag, px per scroll notch

Cst9217 s_touch;
bool s_ok = false;
InputMode s_mode = InputMode::Ui;

struct Gesture {
  bool down = false;
  bool swallowed = false;   // started while the display was off: ignore until lifted
  int16_t x0 = 0, y0 = 0;   // where it started
  int16_t x = 0, y = 0;     // latest
  uint32_t t0 = 0;
  bool moved = false;
  bool longFired = false;
  bool second = false;      // started right after a tap: a double tap, or tap-and-drag
  bool dragging = false;    // trackpad: left button held for a tap-and-drag
  uint8_t maxFingers = 0;
  uint32_t lastMoveEvent = 0;
  int16_t lastEvX = -1, lastEvY = -1;
} g;

uint32_t s_lastTapUpMs = 0;
uint8_t s_emptyReads = 0;  // a lone empty read mid-drag is a glitch, not a release
int16_t s_lastTapX = 0, s_lastTapY = 0;

inline int16_t clampCoord(int v, int max) { return (int16_t)(v < 0 ? 0 : (v > max - 1 ? max - 1 : v)); }
inline int dist2(int dx, int dy) { return dx * dx + dy * dy; }

bool trackpad() { return s_mode == InputMode::Trackpad; }

// Turn a drag on the glass into pointer motion that keeps "up" pointing away from the user even
// after the dial has been turned on the desk: rotate by how far it turned since trackpad mode began.
float s_headingAtEntry = 0;
void sendTrackpadMove(float dx, float dy) {
  const float th = (motionHeading() - s_headingAtEntry) * (float)M_PI / 180.0f;
  const float c = cosf(th), s = sinf(th);
  hostlink::sendMove('t', dx * c - dy * s, dx * s + dy * c);
}

void onPress(int16_t x, int16_t y, uint8_t fingers, uint32_t now) {
  g = Gesture();
  g.down = true;
  g.x0 = g.x = x;
  g.y0 = g.y = y;
  g.t0 = now;
  g.maxFingers = fingers;
  if (!activityBegin(Src::Touch)) {
    g.swallowed = true;
    return;
  }
  g.second = (now - s_lastTapUpMs) < kDoubleTapMs &&
             dist2(x - s_lastTapX, y - s_lastTapY) < kDoubleTapSlopPx * kDoubleTapSlopPx;
  post(Ev::TouchDown, 0, x, y);
  g.lastEvX = x;
  g.lastEvY = y;
  g.lastMoveEvent = now;
}

void onHold(int16_t x, int16_t y, uint8_t fingers, uint32_t now) {
  if (fingers > g.maxFingers) g.maxFingers = fingers;
  const int16_t px = g.x, py = g.y;
  g.x = x;
  g.y = y;
  if (g.swallowed) return;
  activityContinue();

  if (!g.moved && dist2(x - g.x0, y - g.y0) > kTapSlopPx * kTapSlopPx) g.moved = true;

  if (trackpad() && !motionLifted() && g.moved) {
    const float dx = (float)(x - px), dy = (float)(y - py);
    if (g.maxFingers >= 2) {
      // two fingers: scroll (drag down = content down = scroll up, like a touchpad)
      if (dy != 0) hostlink::sendScroll(dy / kTwoFingerScrollPx);
    } else if (dx != 0 || dy != 0) {
      if (g.second && !g.dragging) {
        hostlink::sendButton('l', true);
        g.dragging = true;
      }
      sendTrackpadMove(dx, dy);
    }
  }

  if (!g.moved && !g.longFired && now - g.t0 >= kLongPressMs) {
    g.longFired = true;
    post(Ev::LongPress, 0, x, y);
    // Held up as a pointer, a long press is a thumb resting on the glass, not a right-click
    if (trackpad() && !motionLifted()) hostlink::sendClick('r');
  }

  if ((x != g.lastEvX || y != g.lastEvY) && now - g.lastMoveEvent >= kMoveEventMs) {
    post(Ev::TouchMove, 0, x, y);
    g.lastEvX = x;
    g.lastEvY = y;
    g.lastMoveEvent = now;
  }
}

void onRelease(uint32_t now) {
  const Gesture done = g;
  g.down = false;
  if (done.swallowed) return;
  activityContinue();
  post(Ev::TouchUp, 0, done.x, done.y);

  if (done.dragging) {
    hostlink::sendButton('l', false);
    return;
  }
  const int32_t dur = (int32_t)(now - done.t0);
  const int dx = done.x - done.x0, dy = done.y - done.y0;
  if (!done.moved && !done.longFired && dur < (int32_t)kTapMaxMs) {
    post(Ev::Tap, 0, done.x, done.y);
    if (trackpad()) hostlink::sendClick(done.maxFingers >= 2 ? 'r' : 'l');
    if (done.second) {
      post(Ev::DoubleTap, 0, done.x, done.y);
      s_lastTapUpMs = 0;  // a third tap starts over
    } else {
      s_lastTapUpMs = now;
      s_lastTapX = done.x;
      s_lastTapY = done.y;
    }
    return;
  }
  if (dur <= (int32_t)kSwipeMaxMs && dist2(dx, dy) >= kSwipeMinPx * kSwipeMinPx) {
    char dir = abs(dx) >= abs(dy) ? (dx > 0 ? 'r' : 'l') : (dy > 0 ? 'd' : 'u');
    post(Ev::Swipe, 0, done.x, done.y, dir);
  }
}

}  // namespace

bool touchOk() { return s_ok; }

namespace {
std::atomic<bool> s_fingerDown{false};
std::atomic<uint32_t> s_fingerDownMs{0};
std::atomic<uint32_t> s_fingerUpMs{0};
}  // namespace

bool tapInProgress(uint32_t nowMs, uint32_t afterMs) {
  if (s_fingerDown.load()) return (int32_t)(nowMs - s_fingerDownMs.load()) < (int32_t)kTapMaxMs;
  return (int32_t)(nowMs - s_fingerUpMs.load()) <= (int32_t)afterMs;
}

bool touchBegin() {
  // Reset pulse first: the controller may be wedged from before a soft reboot.
  gpio_config_t rst = {};
  rst.pin_bit_mask = 1ULL << PIN_TP_RST;
  rst.mode = GPIO_MODE_OUTPUT;
  gpio_config(&rst);
  gpio_set_level((gpio_num_t)PIN_TP_RST, 0);
  sleepMs(20);
  gpio_set_level((gpio_num_t)PIN_TP_RST, 1);
  sleepMs(60);

  I2cGuard lock;
  s_touch.setPins(PIN_TP_RST, PIN_TP_INT);
  if (!s_touch.begin(I2C_ADDR_TOUCH)) {
    ESP_LOGW(TAG, "touch: CST9217 not found");
    s_ok = false;
    return false;
  }
  s_touch.setMaxCoordinates(LCD_W, LCD_H);
  s_touch.setMirrorXY(TOUCH_MIRROR_X, TOUCH_MIRROR_Y);
  s_ok = true;
  return true;
}

void touchModeChanged(InputMode mode) {
  if (s_mode == InputMode::Trackpad && mode != InputMode::Trackpad && g.dragging) {
    hostlink::sendButton('l', false);
    g.dragging = false;
  }
  s_mode = mode;
  if (mode == InputMode::Trackpad) s_headingAtEntry = motionHeading();
}

void touchPoll(uint32_t now) {
  if (!s_ok) return;
  // The CST9217 pulses its interrupt line rather than holding it, so it's polled; a read while
  // nothing touches it is short and harmless.
  int16_t xs[2] = {0, 0}, ys[2] = {0, 0};
  uint8_t n;
  {
    I2cGuard lock;
    n = s_touch.getPoint(xs, ys, 2);
  }
  if (n > 0) {
    s_emptyReads = 0;
    if (!s_fingerDown.exchange(true)) s_fingerDownMs = now;
    const int16_t x = clampCoord(xs[0], LCD_W), y = clampCoord(ys[0], LCD_H);
    if (!g.down) onPress(x, y, n, now);
    else onHold(x, y, n, now);
  } else if (g.down && ++s_emptyReads >= 2) {
    s_emptyReads = 0;
    onRelease(now);
    s_fingerDown = false;
    s_fingerUpMs = now;
  }
}

}  // namespace detail
}  // namespace hal
