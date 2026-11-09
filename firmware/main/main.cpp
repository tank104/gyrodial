// Puck: a round dial for Windows. The board, the link to the Windows app, and the faces.
#include <lvgl.h>
#include <esp_log.h>
#include <nvs_flash.h>
#include "puck_clock.h"
#include "hal.h"
#include "hostlink.h"
#include "model.h"
#include "ui.h"

static void initNvs() {
  // Pairings and settings live here, and the Bluetooth controller keeps its calibration here.
  esp_err_t err = nvs_flash_init();
  if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
    ESP_LOGW("puck", "NVS unreadable (%s): erasing it", esp_err_to_name(err));
    nvs_flash_erase();
    err = nvs_flash_init();
  }
  if (err != ESP_OK) ESP_LOGE("puck", "NVS init failed: %s", esp_err_to_name(err));
}

// Runs in the main task, on core 1 (sdkconfig.defaults); Bluetooth and the input task are on core 0.
extern "C" void app_main() {
  initNvs();
  hal::begin();
  hostlink::begin();
  ui::begin();

  uint32_t lastRender = 0;
  for (;;) {
    hostlink::poll();

    InputEvent e;
    while (hal::pollEvent(e)) ui::handleInput(e);

    uint32_t dirty = model_take_dirty();
    if (dirty) ui::update(dirty);

    const uint32_t now = nowMs();
    ui::tick(now);

    // While the panel sleeps its frames are thrown away, so render (and run LVGL's timers) a few
    // times a second instead of 60: animations keep their place without burning the battery.
    if (!hal::displayOn() && (int32_t)(now - lastRender) < 100) {
      sleepMs(5);
      continue;
    }
    lastRender = now;
    const uint32_t wait = lv_timer_handler();
    sleepMs(wait < 5 ? wait : 5);
  }
}
