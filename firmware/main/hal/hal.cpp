// Bringing the board up, the shared I2C bus, and the display's public controls.
#include <string.h>
#include <driver/i2c_master.h>
#include <esp_efuse.h>
#include <esp_heap_caps.h>
#include <esp_mac.h>
#include <esp_system.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>
#include "../config.h"
#include "hal_internal.h"

static const char* TAG = "hal";

namespace hal {
namespace detail {

namespace {
constexpr int kI2cTimeoutMs = 20;
constexpr int kMaxI2cDevices = 8;

SemaphoreHandle_t s_i2c = nullptr;
i2c_master_bus_handle_t s_bus = nullptr;
struct I2cDevice {
  uint8_t addr;
  i2c_master_dev_handle_t handle;
};
I2cDevice s_devices[kMaxI2cDevices];
int s_deviceCount = 0;

// The handle for a 7-bit address, attached to the bus the first time it's used. Under the bus lock.
i2c_master_dev_handle_t device(uint8_t addr) {
  for (int i = 0; i < s_deviceCount; i++)
    if (s_devices[i].addr == addr) return s_devices[i].handle;
  if (!s_bus || s_deviceCount >= kMaxI2cDevices) return nullptr;
  i2c_device_config_t cfg = {};
  cfg.dev_addr_length = I2C_ADDR_BIT_LEN_7;
  cfg.device_address = addr;
  cfg.scl_speed_hz = I2C_HZ;
  i2c_master_dev_handle_t h = nullptr;
  if (i2c_master_bus_add_device(s_bus, &cfg, &h) != ESP_OK) return nullptr;
  s_devices[s_deviceCount++] = {addr, h};
  return h;
}
}  // namespace

void i2cBegin() {
  if (!s_i2c) s_i2c = xSemaphoreCreateRecursiveMutex();
  if (s_bus) return;
  i2c_master_bus_config_t cfg = {};
  cfg.i2c_port = I2C_NUM_0;
  cfg.sda_io_num = (gpio_num_t)PIN_I2C_SDA;
  cfg.scl_io_num = (gpio_num_t)PIN_I2C_SCL;
  cfg.clk_source = I2C_CLK_SRC_DEFAULT;
  cfg.glitch_ignore_cnt = 7;
  cfg.flags.enable_internal_pullup = true;
  if (i2c_new_master_bus(&cfg, &s_bus) != ESP_OK) {
    ESP_LOGE(TAG, "I2C bus init failed");
    s_bus = nullptr;
  } else {
    // A reset in the middle of a transfer (esptool, the reset button, a crash) can leave a chip
    // holding SDA low, waiting for the rest of its byte. Clock it out (9 pulses + STOP) before the
    // first transaction, or that one fails. (The legacy driver did this on install; this one
    // only does it after an error.)
    i2c_master_bus_reset(s_bus);
  }
  // A chip that isn't there (or is busy) NACKs; the callers handle that, so keep the driver quiet.
  esp_log_level_set("i2c.master", ESP_LOG_NONE);
}

void i2cLock() {
  if (s_i2c) xSemaphoreTakeRecursive(s_i2c, portMAX_DELAY);
}

void i2cUnlock() {
  if (s_i2c) xSemaphoreGiveRecursive(s_i2c);
}

bool i2cWrite(uint8_t addr, const uint8_t* data, size_t len) {
  I2cGuard lock;
  i2c_master_dev_handle_t h = device(addr);
  return h && i2c_master_transmit(h, data, len, kI2cTimeoutMs) == ESP_OK;
}

bool i2cRead(uint8_t addr, uint8_t* data, size_t len) {
  I2cGuard lock;
  i2c_master_dev_handle_t h = device(addr);
  return h && i2c_master_receive(h, data, len, kI2cTimeoutMs) == ESP_OK;
}

bool i2cWriteRead(uint8_t addr, const uint8_t* out, size_t outLen, uint8_t* in, size_t inLen) {
  I2cGuard lock;
  i2c_master_dev_handle_t h = device(addr);
  return h && i2c_master_transmit_receive(h, out, outLen, in, inLen, kI2cTimeoutMs) == ESP_OK;
}

bool i2cWriteReg(uint8_t addr, uint8_t reg, uint8_t value) {
  const uint8_t buf[2] = {reg, value};
  return i2cWrite(addr, buf, 2);
}

bool i2cReadRegs(uint8_t addr, uint8_t reg, uint8_t* out, size_t n) { return i2cWriteRead(addr, &reg, 1, out, n); }

bool i2cProbe(uint8_t addr) {
  I2cGuard lock;
  return s_bus && i2c_master_probe(s_bus, addr, kI2cTimeoutMs) == ESP_OK;
}

}  // namespace detail

namespace {
bool s_displayOn = true;
uint8_t s_brightness = 80;  // percent
uint32_t s_displayToggles = 0;  // for diag(): a screen that keeps flicking on and off shows here
}

void begin() {
  detail::inputBegin();
  detail::i2cBegin();

  detail::powerBegin();
  if (!detail::lcdBegin()) ESP_LOGE(TAG, "display init failed");
  detail::lvglBegin();
  detail::lvglRedrawNow();  // an all-black first frame before the light comes up
  setBrightness(s_brightness);

  detail::touchBegin();
  detail::motionBegin();
  detail::buttonsBegin();
  if (!detail::audioBegin()) ESP_LOGW(TAG, "sound is off");
  detail::startInputTask();
}

void setBrightness(uint8_t percent) {
  if (percent > 100) percent = 100;
  s_brightness = percent;
  if (s_displayOn) detail::lcdBrightness((uint8_t)((uint32_t)percent * 255 / 100));
}

void setDisplayOn(bool on) {
  if (on == s_displayOn) return;
  s_displayToggles++;
  if (on) {
    detail::lcdSleep(false);
    detail::lvglSetPanelAwake(true);
    detail::lvglRedrawNow();  // panel RAM went stale while asleep; repaint before it lights up
    detail::lcdCommand(0x29, nullptr, 0);  // DISPON
    s_displayOn = true;
    setBrightness(s_brightness);
  } else {
    detail::lcdBrightness(0);
    detail::lvglSetPanelAwake(false);
    detail::lcdDrain();
    detail::lcdSleep(true);
    s_displayOn = false;
  }
  detail::inputDisplayChanged(on);
}

bool displayOn() { return s_displayOn; }

uint64_t chipId() {
  // The factory MAC's six bytes in memory order, as a little-endian number (what the puck's id
  // has always been built from).
  uint64_t id = 0;
  esp_efuse_mac_get_default(reinterpret_cast<uint8_t*>(&id));
  return id;
}

void diag(char* buf, size_t len) {
  int n = snprintf(buf, len, "up=%lus reset=%d disp=%d bri=%u toggles=%lu wakes=%lu idle=%lums mode=%d touch=%d pmu=%d snd=%d bat=%d/%dmV chg=%d usb=%d heap=%u ",
                   (unsigned long)(nowMs() / 1000), (int)esp_reset_reason(),
                   (int)s_displayOn, (unsigned)s_brightness, (unsigned long)s_displayToggles,
                   (unsigned long)detail::wakesPosted(), (unsigned long)(nowMs() - lastActivityMs()),
                   (int)inputMode(), (int)detail::touchOk(), (int)detail::powerOk(), (int)detail::audioOk(),
                   batteryPercent(), batteryMv(), (int)charging(), (int)usbPowered(),
                   (unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL));
  if (n > 0 && (size_t)n < len) n += detail::eventCounts(buf + n, len - n);
  if (n > 0 && (size_t)n < len) detail::motionDiag(buf + n, len - n);
}

}  // namespace hal
