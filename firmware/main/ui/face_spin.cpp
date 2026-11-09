// Spin: a wheel of eight and a needle. Tap to spin it (it slows on its own and lands on a number),
// or turn the puck to move the needle by hand.
#include <stdio.h>
#include "ui_common.h"

namespace ui {
namespace {

constexpr int SEGMENTS = 8;
constexpr float SEG = 360.0f / SEGMENTS;
constexpr int WHEEL_R = 170;    // middle of the segment band
constexpr int WHEEL_W = 88;
constexpr float NEEDLE_LEN = 112;

lv_obj_t* root;
lv_obj_t* seg[SEGMENTS];
lv_obj_t* num[SEGMENTS];
lv_obj_t* needle;
lv_point_t needlePts[2];
lv_obj_t* hub;
lv_obj_t* hint;

float angle = 0;       // needle, clockwise from 12
float vel = 0;         // degrees per second
bool spinning = false;
bool everSpun = false;
int winner = -1;
uint32_t lastTick = 0;

uint32_t segColor(int i) { return i % 2 == 0 ? COL_YELLOW : COL_GREY3; }

int segmentAt(float a) {
  a = fmodf(a, 360.0f);
  if (a < 0) a += 360.0f;
  return (int)(a / SEG) % SEGMENTS;
}

void drawNeedle() {
  float x, y;
  polar(angle, NEEDLE_LEN, &x, &y);
  setLine(needle, needlePts, CX, CY, x, y);
}

void highlight(int w) {
  winner = w;
  for (int i = 0; i < SEGMENTS; i++) {
    lv_opa_t o = (w < 0 || i == w) ? LV_OPA_COVER : LV_OPA_30;
    lv_obj_set_style_arc_opa(seg[i], o, LV_PART_INDICATOR);
    lv_obj_set_style_text_opa(num[i], o, 0);
  }
}

void create(lv_obj_t* r) {
  root = r;
  for (int i = 0; i < SEGMENTS; i++) {
    seg[i] = arc(root, WHEEL_R, WHEEL_W, segColor(i), false);
    arcSpan(seg[i], i * SEG + 1.5f, (i + 1) * SEG - 1.5f);
    char t[4];
    snprintf(t, sizeof t, "%d", i + 1);
    num[i] = label(root, &nunito_40, i % 2 == 0 ? COL_BG : COL_WHITE, t);
    float x, y;
    polar(i * SEG + SEG / 2, WHEEL_R, &x, &y);
    at(num[i], (int)x, (int)y);
  }
  needle = line(root, needlePts, 16, COL_WHITE);
  hub = circle(root, 46, COL_WHITE);
  at(hub, CX, CY);
  lv_obj_t* dot = circle(hub, 14, COL_BG);
  center(dot, 0, 0);
  // inside the hub's ring of numbers, until the first spin
  hint = label(root, &nunito_22, COL_GREY1, "Tap to spin");
  at(hint, CX, CY + 62);
  drawNeedle();
}

void enter() {
  lastTick = lv_tick_get();
  drawNeedle();
}

void input(const InputEvent& e) {
  switch (e.type) {
    case Ev::Tap:
    case Ev::Shake:
      if (spinning && vel > 400) break;
      vel = (float)rndRange(720, 1260);
      spinning = true;
      if (!everSpun) {
        everSpun = true;
        fadeOut(hint, 200);
      }
      highlight(-1);
      hal::beep(Tone::Tap);
      break;
    case Ev::Turn:
      if (spinning) break;
      {
        int before = segmentAt(angle);
        angle = fmodf(angle + e.value + 360.0f, 360.0f);
        if (segmentAt(angle) != before) hal::beep(Tone::Tick);
        if (winner >= 0) highlight(-1);
        drawNeedle();
      }
      break;
    default:
      break;
  }
}

void tick(uint32_t now) {
  float dt = (int32_t)(now - lastTick) / 1000.0f;
  lastTick = now;
  if (!spinning) return;
  if (dt > 0.05f) dt = 0.05f;
  if (dt < 0) dt = 0;
  int before = segmentAt(angle);
  angle = fmodf(angle + vel * dt, 360.0f);
  // friction that's gentle at speed and firm near the end, so it creeps to a stop
  vel -= (90.0f + 0.45f * vel) * dt;
  if (segmentAt(angle) != before) hal::beep(Tone::Tick);
  if (vel <= 4.0f) {
    vel = 0;
    spinning = false;
    highlight(segmentAt(angle));
    hal::beep(Tone::Select);
  }
  drawNeedle();
}

}  // namespace

const Face FACE_SPIN = {"spin", "Spin", create, enter, nullptr, input, nullptr, tick};

}  // namespace ui
