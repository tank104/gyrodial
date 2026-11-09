// The input task: samples touch, motion, buttons and the PMU on core 0 and hands events to the
// faces through a queue. Also where "is anyone using it?" is tracked, for dimming and waking.
#include <atomic>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/task.h>
#include "hal_internal.h"

namespace hal {
namespace detail {

namespace {

constexpr int kQueueLen = 64;
constexpr uint32_t kPeriodUs = 3000;  // ~333 Hz: the IMU sets the pace; touch/buttons/PMU subsample

QueueHandle_t s_queue = nullptr;
std::atomic<uint32_t> s_lastActivity{0};
std::atomic<bool> s_displayOff{false};
std::atomic<bool> s_wakePosted{false};  // a Wake is out and the display hasn't been switched on yet
std::atomic<InputMode> s_mode{InputMode::Ui};
std::atomic<float> s_sensitivity{1.0f};
std::atomic<bool> s_flip{false};

void inputTask(void*) {
  InputMode lastMode = s_mode.load();
  uint32_t nextUs = nowUs();
  uint32_t lastTouchMs = 0, lastButtonsMs = 0;
  for (;;) {
    const uint32_t tUs = nowUs();
    const uint32_t tMs = nowMs();

    const InputMode mode = s_mode.load();
    if (mode != lastMode) {
      touchModeChanged(mode);
      motionModeChanged(mode);
      lastMode = mode;
    }

    motionPoll(tUs);
    if (tMs - lastTouchMs >= 8) {  // ~120 Hz is plenty for a finger
      lastTouchMs = tMs;
      touchPoll(tMs);
    }
    if (tMs - lastButtonsMs >= 10) {
      lastButtonsMs = tMs;
      buttonsPoll(tMs);
    }
    powerPoll(tMs);

    // Fixed-rate loop; if we fell far behind (a long I2C stall), resync instead of bursting.
    nextUs += kPeriodUs;
    int32_t waitUs = (int32_t)(nextUs - nowUs());
    if (waitUs < -(int32_t)(kPeriodUs * 4)) {
      nextUs = nowUs();
      waitUs = 0;
    }
    if (waitUs >= 1000) vTaskDelay(pdMS_TO_TICKS(waitUs / 1000));
    else vTaskDelay(1);
  }
}

}  // namespace

void inputBegin() {
  if (!s_queue) s_queue = xQueueCreate(kQueueLen, sizeof(InputEvent));
  s_lastActivity = nowMs();
}

std::atomic<uint32_t> s_evCount[(int)Ev::Wake + 1];

bool post(Ev type, float value, int16_t x, int16_t y, char dir) {
  if (!s_queue) return false;
  if ((int)type <= (int)Ev::Wake) s_evCount[(int)type]++;
  InputEvent e;
  e.type = type;
  e.value = value;
  e.x = x;
  e.y = y;
  e.dir = dir;
  e.ms = nowMs();
  // If the faces fall behind, drop the newest rather than block the sampling.
  return xQueueSend(s_queue, &e, 0) == pdTRUE;
}

namespace {
std::atomic<uint32_t> s_wakes{0};
}

bool activityBegin(Src) {
  s_lastActivity = nowMs();
  if (!s_displayOff.load()) return true;
  if (!s_wakePosted.exchange(true)) {
    s_wakes++;
    // Latch only a Wake that got through: a full queue must not leave the screen unwakeable
    if (!post(Ev::Wake)) s_wakePosted = false;
  }
  return false;
}

uint32_t wakesPosted() { return s_wakes.load(); }

int eventCounts(char* buf, size_t len) {
  auto c = [](Ev e) { return (unsigned long)s_evCount[(int)e].load(); };
  return snprintf(buf, len, "ev=turn:%lu,tap:%lu,dbl:%lu,long:%lu,swipe:%lu,down:%lu,btn:%lu,btnlong:%lu,lift:%lu,laid:%lu,shake:%lu ",
                  c(Ev::Turn), c(Ev::Tap), c(Ev::DoubleTap), c(Ev::LongPress), c(Ev::Swipe), c(Ev::TouchDown),
                  c(Ev::Button), c(Ev::ButtonLong), c(Ev::Lifted), c(Ev::Laid), c(Ev::Shake));
}

void activityContinue() { s_lastActivity = nowMs(); }

bool displayIsOff() { return s_displayOff.load(); }

float turnScale() { return s_sensitivity.load() * (s_flip.load() ? -1.0f : 1.0f); }

void startInputTask() {
  // Core 0, beside the Bluetooth host; the main loop (LVGL) has core 1 to itself. The stack has
  // room for hostlink's send* calls, which run here in trackpad mode.
  xTaskCreatePinnedToCore(inputTask, "input", 8192, nullptr, 5, nullptr, 0);
}

// Called by display.cpp's public setDisplayOn (hal.cpp)
void inputDisplayChanged(bool on) {
  s_displayOff = !on;
  if (on) s_wakePosted = false;
  s_lastActivity = nowMs();
}

}  // namespace detail

// ---- public API

bool pollEvent(InputEvent& out) {
  if (!detail::s_queue) return false;
  return xQueueReceive(detail::s_queue, &out, 0) == pdTRUE;
}

void setInputMode(InputMode mode) { detail::s_mode = mode; }
InputMode inputMode() { return detail::s_mode.load(); }
float heading() { return detail::motionHeading(); }
bool lifted() { return detail::motionLifted(); }
uint32_t lastActivityMs() { return detail::s_lastActivity.load(); }

void setTurnSensitivity(float k) {
  if (k < 0.1f) k = 0.1f;
  if (k > 8.0f) k = 8.0f;
  detail::s_sensitivity = k;
}

void setTurnFlip(bool flip) { detail::s_flip = flip; }

}  // namespace hal
