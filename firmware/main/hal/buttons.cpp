// The side buttons. BOOT is a plain GPIO; the PWR key belongs to the AXP2101 and arrives through
// power.cpp as already-classified short/long presses. Both end up as Button / ButtonLong.
#include <driver/gpio.h>
#include "../config.h"
#include "hal_internal.h"

namespace hal {
namespace detail {

namespace {

constexpr uint32_t kDebounceMs = 20;
constexpr uint32_t kLongMs = 800;

bool s_down = false;          // debounced state
bool s_raw = false;
uint32_t s_rawSince = 0;
uint32_t s_pressedAt = 0;
bool s_longFired = false;
bool s_swallowed = false;     // pressed while the display was off: that press only wakes it

}  // namespace

void buttonEvent(bool longPress) {
  // The PMU reports a finished press, so there's no "rest of the gesture" to swallow.
  if (!activityBegin(Src::Button)) return;
  post(longPress ? Ev::ButtonLong : Ev::Button);
}

void buttonsBegin() {
  gpio_config_t io = {};
  io.pin_bit_mask = 1ULL << PIN_BOOT_BTN;
  io.mode = GPIO_MODE_INPUT;
  io.pull_up_en = GPIO_PULLUP_ENABLE;
  gpio_config(&io);
}

void buttonsPoll(uint32_t now) {
  const bool raw = gpio_get_level((gpio_num_t)PIN_BOOT_BTN) == 0;
  if (raw != s_raw) {
    s_raw = raw;
    s_rawSince = now;
  }
  if (raw != s_down && now - s_rawSince >= kDebounceMs) {
    s_down = raw;
    if (s_down) {
      s_pressedAt = now;
      s_longFired = false;
      s_swallowed = !activityBegin(Src::Button);
    } else if (!s_swallowed && !s_longFired) {
      activityContinue();
      post(Ev::Button);
    }
  }
  if (s_down && !s_swallowed && !s_longFired && now - s_pressedAt >= kLongMs) {
    s_longFired = true;
    activityContinue();
    post(Ev::ButtonLong);
  }
}

}  // namespace detail
}  // namespace hal
