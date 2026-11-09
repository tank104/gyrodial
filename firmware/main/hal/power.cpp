// Power: the AXP2101 PMU. Battery level and charging, USB power, and the PWR key.
#include <stddef.h>
#include <atomic>
#include "../config.h"
#include "axp2101.h"
#include "hal_internal.h"

namespace hal {
namespace detail {

namespace {

static const char* TAG = "hal";

constexpr uint32_t kBatteryEveryMs = 2000;
constexpr uint32_t kKeyEveryMs = 50;

Axp2101 s_pmu;
bool s_ok = false;
uint32_t s_lastBattery = 0, s_lastKey = 0;

std::atomic<int> s_percent{-1};
std::atomic<int> s_mv{0};
std::atomic<bool> s_charging{false};
std::atomic<bool> s_usb{false};

// A single-cell LiPo's resting voltage → charge, for while the PMU's gauge has no figure yet
int percentFromMv(int mv) {
  static const int16_t kCurve[][2] = {{4180, 100}, {4100, 90}, {4000, 78}, {3900, 65}, {3800, 50},
                                      {3700, 30},  {3600, 15}, {3500, 7},  {3400, 3},  {3300, 0}};
  if (mv >= kCurve[0][0]) return 100;
  for (size_t i = 1; i < sizeof(kCurve) / sizeof(kCurve[0]); i++) {
    if (mv >= kCurve[i][0]) {
      const int v0 = kCurve[i][0], v1 = kCurve[i - 1][0], p0 = kCurve[i][1], p1 = kCurve[i - 1][1];
      return p0 + (p1 - p0) * (mv - v0) / (v1 - v0);
    }
  }
  return 0;
}

void readBattery() {
  I2cGuard lock;
  const bool battery = s_pmu.isBatteryConnect();
  // Smoothed (~10 s at one reading every 2 s): the cell's voltage jumps with load and charge current
  static float smooth = 0;
  const float raw = battery ? (float)s_pmu.getBattVoltage() : 0;
  smooth = (!battery || smooth == 0) ? raw : smooth + 0.2f * (raw - smooth);
  s_mv = (int)(smooth + 0.5f);
  // The PMU's fuel gauge isn't calibrated for this board's cell (on a 1.75C it read 0 %, then 3 %,
  // at 3.9 V), so the charge comes from the voltage. It reads a little high while charging.
  s_percent = battery && s_mv.load() > 0 ? percentFromMv(s_mv.load()) : -1;
  s_charging = battery && s_pmu.isCharging();
  s_usb = s_pmu.isVbusIn();
}

}  // namespace

bool powerOk() { return s_ok; }

bool powerBegin() {
  I2cGuard lock;
  // The first chip on the bus after a reset: give it a couple of tries before giving up on it
  bool found = false;
  for (int attempt = 0; attempt < 3 && !found; attempt++) {
    if (attempt) sleepMs(5);
    found = s_pmu.begin(I2C_ADDR_PMU);
  }
  if (!found) {
    ESP_LOGW(TAG, "power: AXP2101 not found; no battery readings or PWR key");
    s_ok = false;
    return false;
  }
  // The rails (display, sensors, codec) are left exactly as the PMU powers up, as in Waveshare's
  // examples. Only the charger, gauge inputs and key are set up here.
  s_pmu.disableTSPinMeasure();  // no NTC on this board; with TS measuring, charging would stop
  s_pmu.enableBattDetection();
  s_pmu.enableVbusVoltageMeasure();
  s_pmu.enableBattVoltageMeasure();
  s_pmu.enableSystemVoltageMeasure();
  s_pmu.setChargeTargetVoltage(Axp2101::CHG_VOL_4V2);

  // PWR key: short press and a 1.5 s long press as interrupts (polled; the PMU's IRQ line isn't
  // wired to a GPIO we use). The hardware power-off on a much longer hold stays at its default.
  s_pmu.disableIRQ(Axp2101::ALL_IRQ);
  s_pmu.setIrqLevelTime(Axp2101::IRQ_TIME_1S5);
  s_pmu.clearIrqStatus();
  s_pmu.enableIRQ(Axp2101::PKEY_SHORT_IRQ | Axp2101::PKEY_LONG_IRQ);
  s_ok = true;
  return true;
}

void powerPoll(uint32_t now) {
  if (!s_ok) return;
  if (now - s_lastKey >= kKeyEveryMs) {
    s_lastKey = now;
    bool shortPress = false, longPress = false;
    {
      I2cGuard lock;
      s_pmu.getIrqStatus();
      shortPress = s_pmu.isPekeyShortPressIrq();
      longPress = s_pmu.isPekeyLongPressIrq();
      if (shortPress || longPress) s_pmu.clearIrqStatus();
    }
    if (longPress) buttonEvent(true);
    else if (shortPress) buttonEvent(false);
  }
  if (now - s_lastBattery >= kBatteryEveryMs || s_lastBattery == 0) {
    s_lastBattery = now ? now : 1;
    readBattery();
  }
}

}  // namespace detail

int batteryPercent() { return detail::s_percent.load(); }
int batteryMv() { return detail::s_mv.load(); }
bool charging() { return detail::s_charging.load(); }
bool usbPowered() { return detail::s_usb.load(); }

void powerOff() {
  if (!detail::s_ok) return;
  detail::I2cGuard lock;
  detail::s_pmu.shutdown();
}

}  // namespace hal
