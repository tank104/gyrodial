// Small drawing and animation helpers shared by the faces.
#include <string.h>
#include "ui_common.h"

namespace ui {

lv_obj_t* plain(lv_obj_t* parent) {
  lv_obj_t* o = lv_obj_create(parent);
  lv_obj_remove_style_all(o);
  lv_obj_clear_flag(o, LV_OBJ_FLAG_SCROLLABLE | LV_OBJ_FLAG_CLICKABLE);
  return o;
}

lv_obj_t* box(lv_obj_t* parent, int w, int h, uint32_t color, int radius, lv_opa_t opa) {
  lv_obj_t* o = plain(parent);
  lv_obj_set_size(o, w, h);
  lv_obj_set_style_radius(o, radius, 0);
  lv_obj_set_style_bg_color(o, rgb(color), 0);
  lv_obj_set_style_bg_opa(o, opa, 0);
  return o;
}

lv_obj_t* circle(lv_obj_t* parent, int d, uint32_t color, lv_opa_t opa) {
  return box(parent, d, d, color, LV_RADIUS_CIRCLE, opa);
}

lv_obj_t* ringOutline(lv_obj_t* parent, int d, int border, uint32_t color) {
  lv_obj_t* o = plain(parent);
  lv_obj_set_size(o, d, d);
  lv_obj_set_style_radius(o, LV_RADIUS_CIRCLE, 0);
  lv_obj_set_style_border_width(o, border, 0);
  lv_obj_set_style_border_color(o, rgb(color), 0);
  lv_obj_set_style_border_opa(o, LV_OPA_COVER, 0);
  return o;
}

lv_obj_t* label(lv_obj_t* parent, const lv_font_t* font, uint32_t color, const char* text) {
  lv_obj_t* l = lv_label_create(parent);
  lv_obj_set_style_text_font(l, font, 0);
  lv_obj_set_style_text_color(l, rgb(color), 0);
  lv_obj_set_style_text_align(l, LV_TEXT_ALIGN_CENTER, 0);
  lv_label_set_text(l, text);
  return l;
}

static uint16_t toLv(float degCw) {
  // LVGL measures from 3 o'clock, clockwise; ours from 12 o'clock, clockwise
  int d = (int)lroundf(degCw - 90.0f) % 360;
  if (d < 0) d += 360;
  return (uint16_t)d;
}

lv_obj_t* arc(lv_obj_t* parent, int radius, int width, uint32_t color, bool rounded) {
  lv_obj_t* a = lv_arc_create(parent);
  lv_obj_remove_style_all(a);
  lv_obj_clear_flag(a, LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_SCROLLABLE);
  int d = 2 * radius + width;
  lv_obj_set_size(a, d, d);
  center(a, 0, 0);
  lv_obj_set_style_arc_width(a, width, LV_PART_MAIN);
  lv_obj_set_style_arc_width(a, width, LV_PART_INDICATOR);
  lv_obj_set_style_arc_rounded(a, rounded, LV_PART_MAIN);
  lv_obj_set_style_arc_rounded(a, rounded, LV_PART_INDICATOR);
  lv_obj_set_style_arc_opa(a, LV_OPA_TRANSP, LV_PART_MAIN);
  lv_obj_set_style_arc_color(a, rgb(color), LV_PART_INDICATOR);
  lv_obj_set_style_arc_opa(a, LV_OPA_COVER, LV_PART_INDICATOR);
  lv_arc_set_rotation(a, 0);
  lv_arc_set_bg_angles(a, 0, 360);
  lv_arc_set_angles(a, 0, 0);
  return a;
}

void arcSpan(lv_obj_t* a, float fromDeg, float toDeg) {
  if (toDeg - fromDeg < 0.5f) {
    lv_arc_set_angles(a, 0, 0);
    return;
  }
  if (toDeg - fromDeg >= 359.5f) {
    lv_arc_set_angles(a, 0, 360);
    return;
  }
  uint16_t s = toLv(fromDeg), e = toLv(toDeg);
  if (lv_arc_get_angle_start(a) == s && lv_arc_get_angle_end(a) == e) return;
  lv_arc_set_angles(a, s, e);
}

void arcTrack(lv_obj_t* a, uint32_t color, float fromDeg, float toDeg) {
  lv_obj_set_style_arc_color(a, rgb(color), LV_PART_MAIN);
  lv_obj_set_style_arc_opa(a, LV_OPA_COVER, LV_PART_MAIN);
  if (toDeg - fromDeg >= 359.5f) lv_arc_set_bg_angles(a, 0, 360);
  else lv_arc_set_bg_angles(a, toLv(fromDeg), toLv(toDeg));
}

void center(lv_obj_t* o, int dx, int dy) { lv_obj_align(o, LV_ALIGN_CENTER, dx, dy); }

void at(lv_obj_t* o, int x, int y) { lv_obj_align(o, LV_ALIGN_CENTER, x - CX, y - CY); }

void polar(float degCw, float r, float* x, float* y, int cx, int cy) {
  float a = degCw * PI_F / 180.0f;
  *x = cx + r * sinf(a);
  *y = cy - r * cosf(a);
}

lv_obj_t* line(lv_obj_t* parent, lv_point_t pts[2], int width, uint32_t color) {
  lv_obj_t* l = lv_line_create(parent);
  lv_obj_remove_style_all(l);
  lv_obj_clear_flag(l, LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_SCROLLABLE);
  lv_obj_set_style_line_width(l, width, 0);
  lv_obj_set_style_line_color(l, rgb(color), 0);
  lv_obj_set_style_line_rounded(l, true, 0);
  lv_obj_set_style_line_opa(l, LV_OPA_COVER, 0);
  pts[0].x = pts[0].y = pts[1].x = pts[1].y = 0;
  lv_line_set_points(l, pts, 2);
  return l;
}

void setLine(lv_obj_t* l, lv_point_t pts[2], float x0, float y0, float x1, float y1) {
  // Keep the object's box around the stroke only, so moving it redraws a small area
  int ix0 = (int)lroundf(x0), iy0 = (int)lroundf(y0), ix1 = (int)lroundf(x1), iy1 = (int)lroundf(y1);
  int mx = ix0 < ix1 ? ix0 : ix1, my = iy0 < iy1 ? iy0 : iy1;
  lv_point_t a = {(lv_coord_t)(ix0 - mx), (lv_coord_t)(iy0 - my)};
  lv_point_t b = {(lv_coord_t)(ix1 - mx), (lv_coord_t)(iy1 - my)};
  if (lv_obj_get_x(l) == mx && lv_obj_get_y(l) == my && pts[0].x == a.x && pts[0].y == a.y && pts[1].x == b.x &&
      pts[1].y == b.y)
    return;
  pts[0] = a;
  pts[1] = b;
  lv_obj_set_pos(l, mx, my);
  lv_line_set_points(l, pts, 2);
}

void hide(lv_obj_t* o, bool hidden) {
  if (!o) return;
  if (hidden) lv_obj_add_flag(o, LV_OBJ_FLAG_HIDDEN);
  else lv_obj_clear_flag(o, LV_OBJ_FLAG_HIDDEN);
}

void setText(lv_obj_t* l, const char* text) {
  const char* now = lv_label_get_text(l);
  if (now && strcmp(now, text) == 0) return;
  lv_label_set_text(l, text);
}

// ---- shared words

static void upperInto(char* dst, size_t n, const char* s) {
  size_t i = 0;
  for (; s[i] && i < n - 1; i++) dst[i] = (s[i] >= 'a' && s[i] <= 'z') ? (char)(s[i] - 32) : s[i];
  dst[i] = 0;
}

void setHeader(lv_obj_t* h, const char* text) {
  char up[48];
  upperInto(up, sizeof up, text);
  setText(h, up);
}

lv_obj_t* header(lv_obj_t* parent, const char* text, uint32_t color) {
  lv_obj_t* h = label(parent, &nunito_18, color, "");
  lv_obj_set_style_text_letter_space(h, 3, 0);
  lv_obj_set_width(h, 280);
  lv_label_set_long_mode(h, LV_LABEL_LONG_DOT);
  setHeader(h, text);
  at(h, CX, HEADER_Y);
  return h;
}

lv_obj_t* hintLabel(lv_obj_t* parent, const char* text, int y, int width) {
  lv_obj_t* h = label(parent, &nunito_22, COL_GREY1, text);
  lv_obj_set_width(h, width);
  lv_label_set_long_mode(h, LV_LABEL_LONG_DOT);
  at(h, CX, y);
  return h;
}

// ---- the scroller

static constexpr int FADE_PX = 30;
static constexpr int CUE_R = 222;  // the rim mark's radius (middle of its stroke)

// The fades are LVGL draw masks, added while the box (and so its text) draws and removed after
static void scrollerEvent(lv_event_t* e) {
  Scroller* s = (Scroller*)lv_event_get_user_data(e);
  lv_event_code_t code = lv_event_get_code(e);
  if (code == LV_EVENT_SCROLL) {
    s->refreshCue();
  } else if (code == LV_EVENT_COVER_CHECK) {
    lv_event_set_cover_res(e, LV_COVER_RES_MASKED);
  } else if (code == LV_EVENT_DRAW_MAIN_BEGIN) {
    lv_area_t a;
    lv_obj_get_coords(s->box, &a);
    s->maskTop = s->maskBottom = -1;
    if (lv_obj_get_scroll_top(s->box) > 0) {
      lv_draw_mask_fade_init(&s->fadeTop, &a, LV_OPA_TRANSP, a.y1, LV_OPA_COVER, a.y1 + FADE_PX);
      s->maskTop = lv_draw_mask_add(&s->fadeTop, nullptr);
    }
    if (lv_obj_get_scroll_bottom(s->box) > 0) {
      lv_draw_mask_fade_init(&s->fadeBottom, &a, LV_OPA_COVER, a.y2 - FADE_PX, LV_OPA_TRANSP, a.y2);
      s->maskBottom = lv_draw_mask_add(&s->fadeBottom, nullptr);
    }
  } else if (code == LV_EVENT_DRAW_POST_END) {
    if (s->maskTop >= 0) {
      lv_draw_mask_free_param(&s->fadeTop);
      lv_draw_mask_remove_id(s->maskTop);
      s->maskTop = -1;
    }
    if (s->maskBottom >= 0) {
      lv_draw_mask_free_param(&s->fadeBottom);
      lv_draw_mask_remove_id(s->maskBottom);
      s->maskBottom = -1;
    }
  }
}

// Clockwise degrees from 12 to the point on the right-hand rim at height y
static float rimAngleAt(int y) {
  float c = clampf((float)(CY - y) / CUE_R, -1.0f, 1.0f);
  return acosf(c) * 180.0f / PI_F;
}

void Scroller::create(lv_obj_t* parent, int w, int h, int cx, int cy, const lv_font_t* font, uint32_t color,
                      int lineSpace) {
  box = plain(parent);
  lv_obj_add_flag(box, LV_OBJ_FLAG_SCROLLABLE);
  lv_obj_set_scrollbar_mode(box, LV_SCROLLBAR_MODE_OFF);
  lv_obj_set_scroll_dir(box, LV_DIR_VER);
  lv_obj_set_size(box, w, h);
  at(box, cx, cy);
  text = label(box, font, color, "");
  lv_obj_set_width(text, w);
  lv_label_set_long_mode(text, LV_LABEL_LONG_WRAP);
  lv_obj_set_style_text_line_space(text, lineSpace, 0);
  lv_obj_set_pos(text, 0, 0);
  lv_obj_add_event_cb(box, scrollerEvent, LV_EVENT_ALL, this);

  // the rim mark spans the same height as the box
  float a1 = rimAngleAt(cy - h / 2), a2 = rimAngleAt(cy + h / 2);
  track = arc(parent, CUE_R, 4, COL_GREY3);
  arcSpan(track, a1, a2);
  thumb = arc(parent, CUE_R, 4, COL_GREY1);
  arcSpan(thumb, a1, a1);
  hide(track, true);
  hide(thumb, true);
}

void Scroller::toTop() { lv_obj_scroll_to_y(box, 0, LV_ANIM_OFF); }

void Scroller::setText(const char* t) {
  const char* now = lv_label_get_text(text);
  if (now && strcmp(now, t) == 0) return;
  lv_label_set_text(text, t);
  toTop();
  refreshCue();
}

bool Scroller::overflows() {
  lv_obj_update_layout(box);
  return lv_obj_get_height(text) > lv_obj_get_content_height(box);
}

bool Scroller::scrollBy(int dy, bool animate) {
  lv_obj_update_layout(box);
  int maxY = lv_obj_get_height(text) - lv_obj_get_content_height(box);
  if (maxY <= 0) return false;
  int y = (int)clampf((float)(lv_obj_get_scroll_y(box) + dy), 0, (float)maxY);
  if (y == lv_obj_get_scroll_y(box)) return false;
  lv_obj_scroll_to_y(box, y, animate ? LV_ANIM_ON : LV_ANIM_OFF);
  if (!animate) refreshCue();
  return true;
}

void Scroller::refreshCue() {
  bool more = overflows();
  hide(track, !more);
  hide(thumb, !more);
  if (!more) return;
  float a1 = rimAngleAt(lv_obj_get_y(box)), a2 = rimAngleAt(lv_obj_get_y(box) + lv_obj_get_height(box));
  float total = (float)lv_obj_get_height(text), view = (float)lv_obj_get_content_height(box);
  float top = (float)lv_obj_get_scroll_y(box);
  float f0 = clampf(top / total, 0, 1), f1 = clampf((top + view) / total, 0, 1);
  arcSpan(thumb, a1 + (a2 - a1) * f0, a1 + (a2 - a1) * f1);
}

// ---- animation

static void opaCb(void* o, int32_t v) { lv_obj_set_style_opa((lv_obj_t*)o, (lv_opa_t)v, 0); }
static void tyCb(void* o, int32_t v) { lv_obj_set_style_translate_y((lv_obj_t*)o, (lv_coord_t)v, 0); }
static void popCb(void* o, int32_t v) {
  uintptr_t wh = (uintptr_t)lv_obj_get_user_data((lv_obj_t*)o);
  lv_coord_t w = (lv_coord_t)(wh >> 16), h = (lv_coord_t)(wh & 0xFFFF);
  lv_obj_set_size((lv_obj_t*)o, (lv_coord_t)(w * v / 1000), (lv_coord_t)(h * v / 1000));
}
static void shakeCb(void* o, int32_t v) {
  float t = v / 1000.0f;
  lv_obj_set_style_translate_x((lv_obj_t*)o, (lv_coord_t)lroundf(18.0f * sinf(t * PI_F * 6.0f) * (1.0f - t)), 0);
}
static void hideWhenDone(lv_anim_t* a) {
  lv_obj_t* o = (lv_obj_t*)a->var;
  lv_obj_add_flag(o, LV_OBJ_FLAG_HIDDEN);
  lv_obj_set_style_opa(o, LV_OPA_COVER, 0);
}

void fadeIn(lv_obj_t* o, uint32_t ms, uint32_t delay) {
  lv_anim_del(o, opaCb);
  lv_obj_clear_flag(o, LV_OBJ_FLAG_HIDDEN);
  lv_obj_set_style_opa(o, LV_OPA_TRANSP, 0);
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, o);
  lv_anim_set_exec_cb(&a, opaCb);
  lv_anim_set_values(&a, LV_OPA_TRANSP, LV_OPA_COVER);
  lv_anim_set_time(&a, ms);
  lv_anim_set_delay(&a, delay);
  lv_anim_set_path_cb(&a, lv_anim_path_ease_out);
  lv_anim_start(&a);
}

void fadeOut(lv_obj_t* o, uint32_t ms, bool hideDone) {
  lv_anim_del(o, opaCb);
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, o);
  lv_anim_set_exec_cb(&a, opaCb);
  lv_anim_set_values(&a, lv_obj_get_style_opa(o, 0), LV_OPA_TRANSP);
  lv_anim_set_time(&a, ms);
  lv_anim_set_path_cb(&a, lv_anim_path_ease_in);
  if (hideDone) lv_anim_set_ready_cb(&a, hideWhenDone);
  lv_anim_start(&a);
}

void rise(lv_obj_t* o, int fromDy, uint32_t ms, uint32_t delay) {
  lv_anim_del(o, tyCb);
  lv_obj_set_style_translate_y(o, fromDy, 0);
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, o);
  lv_anim_set_exec_cb(&a, tyCb);
  lv_anim_set_values(&a, fromDy, 0);
  lv_anim_set_time(&a, ms);
  lv_anim_set_delay(&a, delay);
  lv_anim_set_path_cb(&a, lv_anim_path_ease_out);
  lv_anim_start(&a);
}

void shake(lv_obj_t* o) {
  lv_anim_del(o, shakeCb);
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, o);
  lv_anim_set_exec_cb(&a, shakeCb);
  lv_anim_set_values(&a, 0, 1000);
  lv_anim_set_time(&a, 420);
  lv_anim_start(&a);
}

void pop(lv_obj_t* o, uint32_t ms) {
  // Grows the object itself rather than zooming it: LVGL's transform layers are slow on the
  // device (a full off-screen copy per frame). Children keep their size and stay centered, so
  // use it on simple shapes. The resting size is kept in the object's user data, so popping again
  // mid-pop doesn't shrink it.
  if (!lv_obj_get_user_data(o)) {
    lv_obj_update_layout(o);
    uintptr_t wh = ((uintptr_t)lv_obj_get_width(o) << 16) | (uintptr_t)lv_obj_get_height(o);
    lv_obj_set_user_data(o, (void*)wh);
  }
  lv_anim_del(o, popCb);
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, o);
  lv_anim_set_exec_cb(&a, popCb);
  lv_anim_set_values(&a, 600, 1000);
  lv_anim_set_time(&a, ms);
  lv_anim_set_path_cb(&a, lv_anim_path_overshoot);
  lv_anim_start(&a);
}

// ---- numbers

static uint32_t s_rng = 0x2545F491u;

uint32_t rnd() {
  if (s_rng == 0x2545F491u) s_rng ^= (uint32_t)hal::chipId() ^ (lv_tick_get() * 2654435761u);
  s_rng ^= s_rng << 13;
  s_rng ^= s_rng >> 17;
  s_rng ^= s_rng << 5;
  return s_rng;
}

int rndRange(int lo, int hi) { return lo + (int)(rnd() % (uint32_t)(hi - lo + 1)); }

float clampf(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

float approach(float v, float target, float rate) { return v + (target - v) * rate; }

float ease(float v, float target, float dt, float speed) { return v + (target - v) * (1.0f - expf(-speed * dt)); }

float angleDiff(float a, float b) {
  float d = fmodf(a - b, 360.0f);
  if (d > 180.0f) d -= 360.0f;
  if (d < -180.0f) d += 360.0f;
  return d;
}

}  // namespace ui
