// DialKit: the knob of a DialKit panel on a page in your browser (via dialkit-watt). Turn for the
// value, a detent a step; swipe for the next knob; tap to flip a toggle or run an action.
#include <stdio.h>
#include <string.h>
#include "ui_common.h"

namespace ui {
namespace {

constexpr float RING_FROM = 225.0f;  // 7:30, round through 12, to 4:30
constexpr float RING_SPAN = 270.0f;
constexpr int MAX_DOTS = 15;

lv_obj_t* root;
lv_obj_t* dots;
lv_obj_t* panelLbl;
lv_obj_t* nameLbl;
lv_obj_t* valueLbl;
lv_obj_t* ring;
lv_obj_t* boolPill;
lv_obj_t* boolLbl;
lv_obj_t* action;
lv_obj_t* arrowL;
lv_obj_t* arrowR;
lv_obj_t* swatch;
lv_obj_t* hint;

Detents detents;
int dotsFor = -1, dotsAt = -1;
float localVal = 0;
uint32_t localAt = 0;
bool localBool = false;
char valText[48];

float shownVal() { return localAt && lv_tick_elaps(localAt) < 700 ? localVal : g_model.dialVal; }
bool shownBool() { return localAt && lv_tick_elaps(localAt) < 700 ? localBool : g_model.dialBool; }

void layoutDots() {
  int cnt = g_model.dialCnt > MAX_DOTS ? MAX_DOTS : g_model.dialCnt;
  int idx = g_model.dialIdx;
  if (cnt == dotsFor && idx == dotsAt) return;
  dotsFor = cnt;
  dotsAt = idx;
  lv_obj_clean(dots);
  if (cnt <= 1) return;
  // a window of the list when there are more knobs than dots
  int first = 0;
  if (g_model.dialCnt > MAX_DOTS) {
    first = idx - MAX_DOTS / 2;
    if (first < 0) first = 0;
    if (first > g_model.dialCnt - MAX_DOTS) first = g_model.dialCnt - MAX_DOTS;
  }
  const float spacing = 9.5f;
  float start = -spacing * (cnt - 1) / 2.0f;
  for (int i = 0; i < cnt; i++) {
    bool on = first + i == idx;
    lv_obj_t* d = circle(dots, on ? 13 : 7, on ? COL_YELLOW : COL_GREY2);
    float x, y;
    polar(start + i * spacing, 186, &x, &y);
    at(d, (int)x, (int)y);
  }
}

// Whole numbers get the big digits (they have no decimal point); the rest a size that fits
const lv_font_t* valueFont(const char* t) {
  size_t n = strlen(t);
  bool digits = n > 0 && n <= 4;
  for (size_t i = 0; i < n && digits; i++) digits = (t[i] >= '0' && t[i] <= '9') || (i == 0 && t[i] == '-');
  if (digits) return &nunito_digits_110;
  return n > 7 ? &nunito_40 : &nunito_64;
}

void formatNum(float v) {
  float step = g_model.dialStep;
  int decimals = 0;
  if (step > 0 && step < 1) decimals = step < 0.1f ? (step < 0.01f ? 3 : 2) : 1;
  else if (step <= 0 && g_model.dialMax - g_model.dialMin <= 10) decimals = 2;
  snprintf(valText, sizeof valText, "%.*f", decimals, (double)v);
}

void paint() {
  bool on = g_model.dialOn;
  DialKind k = g_model.dialKind;
  hide(valueLbl, !on || k == DialKind::Bool || k == DialKind::Action);
  hide(ring, !on || k != DialKind::Num);
  hide(boolPill, !on || k != DialKind::Bool);
  hide(action, !on || k != DialKind::Action);
  hide(arrowL, !on || k != DialKind::Select);
  hide(arrowR, !on || k != DialKind::Select);
  hide(swatch, !on || k != DialKind::Color);
  hide(dots, !on);

  if (!on) {
    setHeader(panelLbl, "DialKit");
    setText(nameLbl, "No page yet");
    setText(hint, "Open a dialkit-watt page");
    return;
  }
  setHeader(panelLbl, g_model.dialPanel[0] ? g_model.dialPanel : "DialKit");
  setText(nameLbl, g_model.dialName);
  layoutDots();

  switch (k) {
    case DialKind::Num: {
      float v = shownVal();
      if (localAt && lv_tick_elaps(localAt) < 700) formatNum(v);
      else snprintf(valText, sizeof valText, "%s", g_model.dialTxt[0] ? g_model.dialTxt : "");
      if (!valText[0]) formatNum(v);
      setText(valueLbl, valText);
      lv_obj_set_style_text_font(valueLbl, valueFont(valText), 0);
      float span = g_model.dialMax - g_model.dialMin;
      float f = span > 0 ? clampf((v - g_model.dialMin) / span, 0, 1) : 0;
      arcSpan(ring, RING_FROM, RING_FROM + RING_SPAN * f);
      setText(hint, "Turn \xC2\xB7 Swipe: next");
      break;
    }
    case DialKind::Bool: {
      bool b = shownBool();
      lv_obj_set_style_bg_color(boolPill, rgb(b ? COL_YELLOW : COL_GREY3), 0);
      lv_obj_set_style_text_color(boolLbl, rgb(b ? COL_BG : COL_GREY1), 0);
      setText(boolLbl, b ? "ON" : "OFF");
      setText(hint, "Tap \xC2\xB7 Swipe: next");
      break;
    }
    case DialKind::Action:
      setText(hint, "Tap to run \xC2\xB7 Swipe: next");
      break;
    case DialKind::Select:
      setText(valueLbl, g_model.dialTxt);
      lv_obj_set_style_text_font(valueLbl, strlen(g_model.dialTxt) > 8 ? &nunito_28 : &nunito_40, 0);
      setText(hint, "Turn \xC2\xB7 Swipe: next");
      break;
    case DialKind::Color: {
      setText(valueLbl, g_model.dialTxt);
      lv_obj_set_style_text_font(valueLbl, &nunito_22, 0);
      unsigned c = 0;
      const char* t = g_model.dialTxt[0] == '#' ? g_model.dialTxt + 1 : g_model.dialTxt;
      if (sscanf(t, "%6x", &c) == 1) lv_obj_set_style_bg_color(swatch, rgb(c), 0);
      setText(hint, "Swipe: next");
      break;
    }
    case DialKind::Text:
      setText(valueLbl, g_model.dialTxt);
      lv_obj_set_style_text_font(valueLbl, &nunito_28, 0);
      setText(hint, "Swipe: next");
      break;
  }
  if (k == DialKind::Color) at(valueLbl, CX, 320);
  else at(valueLbl, CX, 238);
}

void create(lv_obj_t* r) {
  root = r;
  ring = arc(root, 205, 14, COL_YELLOW);
  arcTrack(ring, COL_GREY4, RING_FROM, RING_FROM + RING_SPAN);
  dots = plain(root);
  lv_obj_set_size(dots, LCD_W, LCD_H);

  panelLbl = header(root, "DialKit");
  lv_obj_set_width(panelLbl, 250);
  nameLbl = label(root, &nunito_28, COL_WHITE, "");
  lv_obj_set_width(nameLbl, 320);
  lv_label_set_long_mode(nameLbl, LV_LABEL_LONG_DOT);
  at(nameLbl, CX, 116);

  valueLbl = label(root, &nunito_64, COL_WHITE, "");
  lv_obj_set_width(valueLbl, 320);
  lv_label_set_long_mode(valueLbl, LV_LABEL_LONG_DOT);
  at(valueLbl, CX, 238);

  boolPill = box(root, 176, 84, COL_GREY3, LV_RADIUS_CIRCLE);
  at(boolPill, CX, 240);
  boolLbl = label(boolPill, &nunito_40, COL_GREY1, "OFF");
  center(boolLbl, 0, 0);

  action = ringOutline(root, 136, 6, COL_YELLOW);
  at(action, CX, 240);
  lv_obj_t* run = label(action, &nunito_28, COL_YELLOW, "Run");
  center(run, 0, 0);

  arrowL = label(root, &nunito_64, COL_GREY2, "\xE2\x80\xB9");  // ‹
  at(arrowL, CX - 160, 236);
  arrowR = label(root, &nunito_64, COL_GREY2, "\xE2\x80\xBA");  // ›
  at(arrowR, CX + 160, 236);

  swatch = circle(root, 120, COL_WHITE);
  lv_obj_set_style_border_width(swatch, 3, 0);
  lv_obj_set_style_border_color(swatch, rgb(COL_GREY3), 0);
  lv_obj_set_style_border_opa(swatch, LV_OPA_COVER, 0);
  at(swatch, CX, 236);

  hint = hintLabel(root, "");
}

void enter() {
  dotsFor = dotsAt = -1;
  localAt = 0;
  detents.reset();
  paint();
}

void input(const InputEvent& e) {
  if (!g_model.dialOn) return;
  switch (e.type) {
    case Ev::Turn: {
      int n = detents.feed(e.value, 10.0f);
      if (!n) break;
      hostlink::sendDial("turn", n);
      hal::beep(Tone::Tick);
      if (g_model.dialKind == DialKind::Num) {
        float step = g_model.dialStep > 0 ? g_model.dialStep : (g_model.dialMax - g_model.dialMin) / 100.0f;
        localVal = clampf(shownVal() + n * step, g_model.dialMin, g_model.dialMax);
        localAt = lv_tick_get();
        paint();
      }
      break;
    }
    case Ev::Swipe:
      if (e.dir == 'l' || e.dir == 'r') {
        hostlink::sendDial(e.dir == 'l' ? "prev" : "next");
        hal::beep(Tone::Select);
        localAt = 0;
        rise(nameLbl, e.dir == 'l' ? -10 : 10, 200);
      }
      break;
    case Ev::Tap:
      hostlink::sendDial("tap");
      hal::beep(Tone::Tap);
      if (g_model.dialKind == DialKind::Bool) {
        localBool = !shownBool();
        localAt = lv_tick_get();
        paint();
      } else if (g_model.dialKind == DialKind::Action) {
        pop(action, 280);
      }
      break;
    default:
      break;
  }
}

void update(uint32_t dirty) {
  if (dirty & D_DIAL) {
    localAt = 0;
    paint();
  }
}

}  // namespace

const Face FACE_DIALKIT = {"dialkit", "DialKit", create, enter, nullptr, input, update, nullptr};

}  // namespace ui
