// Trackpad: flat on the desk the glass moves the pointer and turning scrolls; lifted, the hand's
// aim moves it. The hal sends all of the mouse traffic itself (InputMode::Trackpad), so this face
// only draws what's happening.
#include "ui_common.h"

namespace ui {
namespace {

lv_obj_t* root;
lv_obj_t* padView;      // flat: the dot grid
lv_obj_t* aimView;      // lifted: the reticle
lv_obj_t* header;
lv_obj_t* hint;
lv_obj_t* glowOuter;
lv_obj_t* glowInner;
lv_obj_t* ripple;
lv_obj_t* scrollHint;
lv_obj_t* reticle;
bool pointing = false;
bool touching = false;
uint32_t lastScroll = 0;
bool scrollShown = false;

void rippleSizeCb(void* o, int32_t v) {
  lv_obj_t* r = (lv_obj_t*)o;
  lv_coord_t cx = lv_obj_get_x(r) + lv_obj_get_width(r) / 2;
  lv_coord_t cy = lv_obj_get_y(r) + lv_obj_get_height(r) / 2;
  lv_obj_set_size(r, v, v);
  lv_obj_set_pos(r, cx - v / 2, cy - v / 2);
  lv_obj_set_style_border_opa(r, (lv_opa_t)(255 - (v - 24) * 255 / 136), 0);
}

void rippleAt(int x, int y) {
  lv_anim_del(ripple, rippleSizeCb);
  lv_obj_set_size(ripple, 24, 24);
  lv_obj_set_pos(ripple, x - 12, y - 12);
  hide(ripple, false);
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, ripple);
  lv_anim_set_exec_cb(&a, rippleSizeCb);
  lv_anim_set_values(&a, 24, 160);
  lv_anim_set_time(&a, 380);
  lv_anim_set_path_cb(&a, lv_anim_path_ease_out);
  lv_anim_set_ready_cb(&a, [](lv_anim_t* an) { lv_obj_add_flag((lv_obj_t*)an->var, LV_OBJ_FLAG_HIDDEN); });
  lv_anim_start(&a);
}

void glowAt(int x, int y) {
  at(glowOuter, x, y);
  at(glowInner, x, y);
}

void setPointing(bool p) {
  pointing = p;
  hide(padView, p);
  hide(aimView, !p);
  setHeader(header, p ? "Pointer" : "Trackpad");
  setText(hint, p ? "Aim \xC2\xB7 Tap to click" : "Lift to point");
  hide(glowOuter, true);
  hide(glowInner, true);
  reportFace(p ? "pointer" : "trackpad");
  if (p) rise(reticle, 16, 260);
}

void create(lv_obj_t* r) {
  root = r;

  padView = plain(root);
  lv_obj_set_size(padView, LCD_W, LCD_H);
  // a fine grid of dots inside the circle: the glass as a surface
  const int step = 36;
  for (int y = CY - 6 * step; y <= CY + 6 * step; y += step)
    for (int x = CX - 6 * step; x <= CX + 6 * step; x += step) {
      int dx = x - CX, dy = y - CY;
      if (dx * dx + dy * dy > 186 * 186 || y < 96 || y > 366) continue;  // clear of the words
      lv_obj_t* d = circle(padView, 5, COL_GREY3);
      at(d, x, y);
    }

  aimView = plain(root);
  lv_obj_set_size(aimView, LCD_W, LCD_H);
  reticle = plain(aimView);
  lv_obj_set_size(reticle, 170, 170);
  at(reticle, CX, CY - 10);
  lv_obj_t* ring = ringOutline(reticle, 128, 5, COL_YELLOW);
  center(ring, 0, 0);
  // four ticks with a gap at the ring, pointing in
  static lv_point_t tickPts[4][2];
  const int dirs[4][2] = {{0, -1}, {1, 0}, {0, 1}, {-1, 0}};
  for (int i = 0; i < 4; i++) {
    lv_obj_t* t = line(reticle, tickPts[i], 5, COL_YELLOW);
    float x0 = 85 + dirs[i][0] * 82.0f, y0 = 85 + dirs[i][1] * 82.0f;
    float x1 = 85 + dirs[i][0] * 50.0f, y1 = 85 + dirs[i][1] * 50.0f;
    setLine(t, tickPts[i], x0, y0, x1, y1);
  }
  lv_obj_t* dot = circle(reticle, 12, COL_WHITE);
  center(dot, 0, 0);
  hide(aimView, true);

  header = ui::header(root, "Trackpad");
  hint = hintLabel(root, "Lift to point");

  glowOuter = circle(root, 92, COL_YELLOW, LV_OPA_20);
  glowInner = circle(root, 34, COL_YELLOW);
  hide(glowOuter, true);
  hide(glowInner, true);

  ripple = ringOutline(root, 24, 4, COL_YELLOW);
  hide(ripple, true);

  scrollHint = label(root, &nunito_28, COL_YELLOW, LV_SYMBOL_DOWN);
  at(scrollHint, CX, 352);
  hide(scrollHint, true);
}

void enter() {
  hal::setInputMode(InputMode::Trackpad);
  touching = false;
  scrollShown = false;
  hide(scrollHint, true);
  setPointing(hal::lifted());
}

void leave() {
  hal::setInputMode(InputMode::Ui);
  lv_anim_del(ripple, rippleSizeCb);
  hide(ripple, true);
}

void input(const InputEvent& e) {
  switch (e.type) {
    case Ev::Lifted:
      if (!pointing) setPointing(true);
      break;
    case Ev::Laid:
      if (pointing) setPointing(false);
      break;
    case Ev::TouchDown:
      if (pointing) break;
      touching = true;
      glowAt(e.x, e.y);
      fadeIn(glowOuter, 120);
      fadeIn(glowInner, 120);
      break;
    case Ev::TouchMove:
      if (!pointing && touching) glowAt(e.x, e.y);
      break;
    case Ev::TouchUp:
      if (touching) {
        touching = false;
        fadeOut(glowOuter, 220);
        fadeOut(glowInner, 220);
      }
      break;
    case Ev::Tap:
    case Ev::DoubleTap:
      if (pointing) {
        rippleAt(CX, CY - 10);
      } else {
        rippleAt(e.x, e.y);
      }
      break;
    case Ev::LongPress:
      // the hal right-clicks; show it with a double ring
      rippleAt(pointing ? CX : e.x, pointing ? CY - 10 : e.y);
      break;
    case Ev::Turn:
      if (pointing) break;
      setText(scrollHint, e.value > 0 ? LV_SYMBOL_DOWN : LV_SYMBOL_UP);
      if (!scrollShown) {
        scrollShown = true;
        fadeIn(scrollHint, 100);
      }
      lastScroll = lv_tick_get();
      break;
    default:
      break;
  }
}

void tick(uint32_t now) {
  if (scrollShown && (int32_t)(now - lastScroll) > 450) {
    scrollShown = false;
    fadeOut(scrollHint, 250);
  }
}

}  // namespace

const Face FACE_TRACKPAD = {"trackpad", "Trackpad", create, enter, leave, input, nullptr, tick};

}  // namespace ui
