// Dice: one to three of them (turn to change how many). Tap, or give the puck a shake, to roll.
#include <stdio.h>
#include "ui_common.h"

namespace ui {
namespace {

constexpr int MAX_DICE = 3;
constexpr uint32_t TUMBLE_MS = 620;

// Where the pips sit on a 3x3 grid: TL TR ML C MR BL BR
const int8_t PIP_POS[7][2] = {{-1, -1}, {1, -1}, {-1, 0}, {0, 0}, {1, 0}, {-1, 1}, {1, 1}};
const uint8_t PIPS_FOR[7] = {
    0,
    0b0001000,  // 1: C
    0b1000001,  // 2: TL BR
    0b1001001,  // 3: TL C BR
    0b1100011,  // 4: TL TR BL BR
    0b1101011,  // 5: 4 + C
    0b1110111,  // 6: TL TR ML MR BL BR
};

lv_obj_t* root;
lv_obj_t* tray;
lv_obj_t* die[MAX_DICE];
lv_obj_t* pip[MAX_DICE][7];
lv_obj_t* countLbl;
lv_obj_t* totalLbl;
lv_obj_t* hint;

int count = 2;
int value[MAX_DICE] = {5, 2, 6};
uint32_t rollStart = 0;
uint32_t lastFlip = 0;
Detents detents;

void showValue(int d, int v) {
  for (int p = 0; p < 7; p++) hide(pip[d][p], !(PIPS_FOR[v] & (1 << (6 - p))));
}

void build() {
  lv_obj_clean(tray);
  int size = count == 1 ? 156 : (count == 2 ? 124 : 102);
  int gap = count == 3 ? 16 : 30;
  int total = count * size + (count - 1) * gap;
  for (int d = 0; d < count; d++) {
    die[d] = box(tray, size, size, COL_WHITE, size / 5);
    int cx = -total / 2 + size / 2 + d * (size + gap);
    center(die[d], cx, 0);
    int pd = size * 17 / 100;
    int off = size * 27 / 100;
    for (int p = 0; p < 7; p++) {
      pip[d][p] = circle(die[d], pd, COL_BG);
      center(pip[d][p], PIP_POS[p][0] * off, PIP_POS[p][1] * off);
    }
    showValue(d, value[d]);
  }
  setHeader(countLbl, count == 1 ? "1 die" : (count == 2 ? "2 dice" : "3 dice"));
  hide(totalLbl, true);
}

void showTotal() {
  int sum = 0;
  for (int d = 0; d < count; d++) sum += value[d];
  if (count > 1) {
    lv_label_set_text_fmt(totalLbl, "Total %d", sum);
    fadeIn(totalLbl, 200);
  }
}

void roll() {
  rollStart = lv_tick_get();
  if (rollStart == 0) rollStart = 1;
  lastFlip = 0;
  hide(totalLbl, true);
  hide(hint, true);
}

void create(lv_obj_t* r) {
  root = r;
  countLbl = header(root, "");
  tray = plain(root);
  lv_obj_set_size(tray, 400, 200);
  at(tray, CX, CY - 12);
  totalLbl = label(root, &nunito_28, COL_GREY1, "");
  at(totalLbl, CX, 332);
  hint = hintLabel(root, "Turn: dice \xC2\xB7 Tap: roll");
  build();
}

void enter() {
  detents.reset();
  rollStart = 0;
  for (int d = 0; d < count; d++) lv_obj_set_style_translate_y(die[d], 0, 0);
}

void input(const InputEvent& e) {
  switch (e.type) {
    case Ev::Tap:
    case Ev::Shake:
      if (!rollStart) {
        hal::beep(Tone::Tap);
        roll();
      }
      break;
    case Ev::Turn: {
      if (rollStart) break;
      int n = detents.feed(e.value, 30.0f);
      int c = count + n;
      if (c < 1) c = 1;
      if (c > MAX_DICE) c = MAX_DICE;
      if (c != count) {
        count = c;
        hal::beep(Tone::Tick);
        build();
      }
      break;
    }
    default:
      break;
  }
}

void tick(uint32_t now) {
  if (!rollStart) return;
  int32_t elapsed = (int32_t)(now - rollStart);
  uint32_t t = elapsed > 0 ? (uint32_t)elapsed : 0;
  if (t < TUMBLE_MS) {
    // tumbling: new faces, a little jump, quicker at first then slowing
    uint32_t every = 55 + t / 8;
    if ((int32_t)(now - lastFlip) >= (int32_t)every) {
      lastFlip = now;
      for (int d = 0; d < count; d++) {
        int v;
        do v = rndRange(1, 6);
        while (v == value[d]);
        value[d] = v;
        showValue(d, v);
        lv_obj_set_style_translate_y(die[d], (lv_coord_t)(-rndRange(4, 22) * (int)(TUMBLE_MS - t) / (int)TUMBLE_MS), 0);
        lv_obj_set_style_translate_x(die[d], (lv_coord_t)rndRange(-6, 6), 0);
      }
      hal::beep(Tone::Tick);
    }
    return;
  }
  rollStart = 0;
  for (int d = 0; d < count; d++) {
    lv_obj_set_style_translate_x(die[d], 0, 0);
    rise(die[d], -10, 220, d * 40);
  }
  hal::beep(Tone::Select);
  showTotal();
  hide(hint, false);
}

}  // namespace

const Face FACE_DICE = {"dice", "Dice", create, enter, nullptr, input, nullptr, tick};

}  // namespace ui
