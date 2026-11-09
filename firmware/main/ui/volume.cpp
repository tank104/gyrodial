// The system volume, as the dial sees it. The puck owns the number while it's being turned (the
// app's echo is ignored for a moment), and the app owns it the rest of the time.
#include "ui_common.h"

namespace ui {
namespace vol {

static float s_vol = 50;
static uint32_t s_lastTurn = 0;
static int s_sent = -1;
static bool s_muteLocal = false;
static uint32_t s_muteLocalAt = 0;
static bool s_everTurned = false;

static constexpr uint32_t ECHO_IGNORE_MS = 800;

bool recentlyTurned(uint32_t ms) { return s_everTurned && lv_tick_elaps(s_lastTurn) < ms; }

void syncFromModel() {
  if (!recentlyTurned(ECHO_IGNORE_MS)) s_vol = (float)g_model.vol;
}

float value() { return s_vol; }

bool muted() {
  if (s_muteLocalAt && lv_tick_elaps(s_muteLocalAt) < ECHO_IGNORE_MS) return s_muteLocal;
  return g_model.mute;
}

void toggleMute() {
  s_muteLocal = !muted();
  s_muteLocalAt = lv_tick_get();
  hostlink::sendMute(s_muteLocal);
  hal::beep(Tone::Tap);
}

void turn(float deg) {
  if (!recentlyTurned(ECHO_IGNORE_MS)) s_vol = (float)g_model.vol;
  // Turning up out of mute unmutes, as the volume keys do
  if (deg > 0 && muted()) {
    s_muteLocal = false;
    s_muteLocalAt = lv_tick_get();
    hostlink::sendMute(false);
  }
  float before = s_vol;
  s_vol = clampf(s_vol + deg * 0.35f, 0.0f, 100.0f);
  int v = (int)lroundf(s_vol);
  if (v != s_sent) {
    hostlink::sendVolume(v);
    s_sent = v;
  }
  if ((int)(before / 5.0f) != (int)(s_vol / 5.0f)) hal::beep(Tone::Tick);
  s_lastTurn = lv_tick_get();
  s_everTurned = true;
}

}  // namespace vol

// ---------------------------------------------------------------- the view

static constexpr float RING_FROM = 210.0f;  // 7 o'clock, round through 12, to 5 o'clock
static constexpr float RING_SPAN = 300.0f;

void VolumeView::create(lv_obj_t* parent) {
  box = plain(parent);
  lv_obj_set_size(box, LCD_W, LCD_H);
  lv_obj_set_pos(box, 0, 0);

  ring = arc(box, 211, 16, COL_YELLOW);
  arcTrack(ring, COL_GREY4, RING_FROM, RING_FROM + RING_SPAN);

  num = label(box, &nunito_digits_150, COL_WHITE, "50");
  at(num, CX, CY - 10);
  sub = label(box, &nunito_18, COL_GREY1, "VOLUME");
  lv_obj_set_style_text_letter_space(sub, 3, 0);
  at(sub, CX, CY + 88);

  lv_obj_add_flag(box, LV_OBJ_FLAG_HIDDEN);
}

void VolumeView::refresh() {
  int v = (int)lroundf(vol::value());
  bool m = vol::muted();
  if (v != lastNum) {
    lv_label_set_text_fmt(num, "%d", v);
    arcSpan(ring, RING_FROM, RING_FROM + RING_SPAN * v / 100.0f);
    lastNum = v;
  }
  if ((int)m != lastMuted) {
    lv_obj_set_style_text_color(num, rgb(m ? COL_GREY2 : COL_WHITE), 0);
    lv_obj_set_style_arc_color(ring, rgb(m ? COL_GREY2 : COL_YELLOW), LV_PART_INDICATOR);
    lv_obj_set_style_text_color(sub, rgb(m ? COL_RED : COL_GREY1), 0);
    lv_label_set_text(sub, m ? "MUTED" : "VOLUME");
    lastMuted = m;
  }
}

void VolumeView::show() {
  refresh();
  lastShowMs = lv_tick_get();
  if (!shown) {
    shown = true;
    fadeIn(box, 140);
  }
}

bool VolumeView::tick(uint32_t now) {
  if (!shown) return false;
  refresh();
  if ((int32_t)(now - lastShowMs) > 1500 && !vol::recentlyTurned(1500)) {
    shown = false;
    fadeOut(box, 260);
    return false;
  }
  return true;
}

void VolumeView::hideNow() {
  shown = false;
  lv_obj_add_flag(box, LV_OBJ_FLAG_HIDDEN);
}

}  // namespace ui
