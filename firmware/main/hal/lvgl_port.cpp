// LVGL 8 on the CO5300: two DMA draw buffers in internal RAM, and an asynchronous flush. LVGL
// renders into one buffer while the other is still going out over QSPI; the SPI ISR tells LVGL
// when a flush is done.
#include <lvgl.h>
#include <esp_heap_caps.h>
#include "../config.h"
#include "hal_internal.h"

static const char* TAG = "hal";

namespace hal {
namespace detail {

namespace {

// 32 full-width rows = 29,824 bytes: one SPI transaction per flush (the cap is 32 KiB).
constexpr int kBufRows = 32;
constexpr uint32_t kBufPixels = (uint32_t)LCD_W * kBufRows;

lv_disp_draw_buf_t s_drawBuf;
lv_disp_drv_t s_drv;
volatile bool s_panelAwake = true;

// Runs in the SPI ISR (not in IRAM: see CONFIG_SPI_MASTER_ISR_IN_IRAM in sdkconfig.defaults)
void flushDone(void* arg) { lv_disp_flush_ready(static_cast<lv_disp_drv_t*>(arg)); }

// The CO5300 takes windows that start on an even column/row and end on an odd one.
void rounder(lv_disp_drv_t*, lv_area_t* a) {
  a->x1 &= ~1;
  a->y1 &= ~1;
  a->x2 |= 1;
  a->y2 |= 1;
}

void flush(lv_disp_drv_t* drv, const lv_area_t* a, lv_color_t* px) {
  if (!s_panelAwake) {
    // The panel is asleep: drop the frame. Waking it redraws everything.
    lv_disp_flush_ready(drv);
    return;
  }
  const size_t bytes = (size_t)(a->x2 - a->x1 + 1) * (a->y2 - a->y1 + 1) * sizeof(lv_color_t);
  lcdDrain();  // the previous flush is finished (LVGL waited for it); collect its result
  lcdSetWindow(a->x1, a->y1, a->x2, a->y2);
  // LV_COLOR_16_SWAP is 1, so the buffer is already big-endian RGB565, as the panel wants it.
  if (!lcdQueuePixels(px, bytes, flushDone, drv)) lv_disp_flush_ready(drv);
}

}  // namespace

void lvglBegin() {
  lv_init();
  auto* buf1 = (lv_color_t*)heap_caps_malloc(kBufPixels * sizeof(lv_color_t), MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL);
  auto* buf2 = (lv_color_t*)heap_caps_malloc(kBufPixels * sizeof(lv_color_t), MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL);
  if (!buf1) {
    ESP_LOGE(TAG, "lvgl: no DMA memory for the draw buffer");
    return;
  }
  if (!buf2) ESP_LOGW(TAG, "lvgl: single draw buffer (low DMA memory)");
  lv_disp_draw_buf_init(&s_drawBuf, buf1, buf2, kBufPixels);

  lv_disp_drv_init(&s_drv);
  s_drv.hor_res = LCD_W;
  s_drv.ver_res = LCD_H;
  s_drv.flush_cb = flush;
  s_drv.rounder_cb = rounder;
  s_drv.draw_buf = &s_drawBuf;
  lv_disp_t* disp = lv_disp_drv_register(&s_drv);
  // Black, so the round panel's corners (and anything a face leaves uncovered) stay off.
  lv_obj_set_style_bg_color(lv_scr_act(), lv_color_black(), 0);
  lv_obj_set_style_bg_opa(lv_scr_act(), LV_OPA_COVER, 0);
  lv_disp_set_bg_color(disp, lv_color_black());
}

void lvglSetPanelAwake(bool awake) { s_panelAwake = awake; }

void lvglRedrawNow() {
  lv_disp_t* disp = lv_disp_get_default();
  if (!disp) return;
  lv_obj_invalidate(lv_disp_get_scr_act(disp));
  lv_obj_invalidate(lv_disp_get_layer_top(disp));
  lv_obj_invalidate(lv_disp_get_layer_sys(disp));
  lv_refr_now(disp);
  lcdDrain();
}

}  // namespace detail
}  // namespace hal
