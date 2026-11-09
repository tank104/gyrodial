// Pair: a PC this dial doesn't know wants to connect. Dial in three numbers, like a combination
// lock: turn to each one and tap. It proves someone is holding this dial. The side button says no.
// When it ends, a card says how (paired, cancelled, or stopped) for a moment before leaving.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "ui_common.h"

namespace ui {
namespace {

constexpr int STEPS = 40;               // numbers on the dial
constexpr float PER_STEP = 360.0f / STEPS;
constexpr uint32_t PAIR_MS = 180000;    // hostlink gives up after 3 minutes
constexpr uint32_t KEEP_NUMBERS_MS = 120000;  // the same PC asking again soon gets the same numbers
constexpr uint32_t RESULT_MS = 2000;

enum class End : uint8_t { None, Paired, Cancelled };

lv_obj_t* root;
lv_obj_t* content;
lv_obj_t* hostLbl;
lv_obj_t* chipsRow;
lv_obj_t* chip[3];
lv_obj_t* chipLbl[3];
lv_obj_t* valueLbl;
lv_obj_t* marker;
lv_obj_t* countdown;
lv_obj_t* card;
lv_obj_t* cardIcon;
lv_obj_t* cardIconLbl;
lv_obj_t* cardTitle;
lv_obj_t* cardSub;
lv_point_t tickPts[STEPS][2];

int target[3];
int stage = 0;           // which number we're on; 3 = all done
int value = 0;
Detents detents;
char targetsFor[32] = "";
uint32_t targetsAt = 0;
bool haveTargets = false;
uint32_t startedAt = 0;
End ending = End::None;  // how this face ended it (None: the PC went, or time ran out)
uint32_t cardAt = 0;     // the result card is up since (0: dialling)
char host[32] = "";

const char* hostName() { return g_model.pairHost[0] ? g_model.pairHost : "this PC"; }

void paintChips() {
  for (int i = 0; i < 3; i++) {
    bool isDone = i < stage, isNow = i == stage;
    lv_obj_set_style_bg_color(chip[i], rgb(isDone ? COL_GREEN : COL_GREY4), 0);
    lv_obj_set_style_border_color(chip[i], rgb(isNow ? COL_YELLOW : (isDone ? COL_GREEN : COL_GREY3)), 0);
    lv_obj_set_style_text_color(chipLbl[i], rgb(isDone ? COL_BG : (isNow ? COL_WHITE : COL_GREY1)), 0);
  }
}

void paintValue() {
  lv_label_set_text_fmt(valueLbl, "%d", value);
  float x, y;
  polar(value * PER_STEP, 190, &x, &y);
  at(marker, (int)x, (int)y);
  bool match = stage < 3 && value == target[stage];
  lv_obj_set_style_text_color(valueLbl, rgb(match ? COL_YELLOW : COL_WHITE), 0);
}

void paintCountdown(uint32_t now) {
  int32_t gone = (int32_t)(now - startedAt);
  float left = 1.0f - clampf((float)gone / PAIR_MS, 0, 1);
  arcSpan(countdown, 360.0f * (1.0f - left), 360.0f);
}

void newTargets(uint32_t now) {
  int prev = value;
  for (int i = 0; i < 3; i++) {
    int t;
    // far enough from the last one that it takes a real turn
    do t = rndRange(0, STEPS - 1);
    while (abs(t - prev) < 6 || abs(t - prev) > STEPS - 6);
    target[i] = t;
    prev = t;
    char s[4];
    snprintf(s, sizeof s, "%d", t);
    lv_label_set_text(chipLbl[i], s);
  }
  strncpy(targetsFor, hostName(), sizeof targetsFor - 1);
  targetsFor[sizeof targetsFor - 1] = 0;
  targetsAt = now;
  haveTargets = true;
}

// A pairing (re)starts: same numbers if the same PC asked a moment ago, so someone halfway through
// copying them down isn't sent back to new ones
void startDialling() {
  uint32_t now = lv_tick_get();
  strncpy(host, hostName(), sizeof host - 1);
  host[sizeof host - 1] = 0;
  setText(hostLbl, host);
  value = 0;
  stage = 0;
  detents.reset();
  if (!haveTargets || strcmp(targetsFor, host) != 0 || (int32_t)(now - targetsAt) > (int32_t)KEEP_NUMBERS_MS)
    newTargets(now);
  startedAt = now;
  ending = End::None;
  cardAt = 0;
  hide(card, true);
  hide(content, false);
  lv_obj_set_style_opa(content, LV_OPA_COVER, 0);
  paintChips();
  paintValue();
  paintCountdown(now);
}

void showCard(End how) {
  cardAt = lv_tick_get() | 1;
  uint32_t bg = how == End::Paired ? COL_GREEN : COL_GREY3;
  lv_obj_set_style_bg_color(cardIcon, rgb(bg), 0);
  lv_obj_set_style_text_color(cardIconLbl, rgb(how == End::Paired ? COL_BG : COL_WHITE), 0);
  switch (how) {
    case End::Paired:
      setText(cardIconLbl, LV_SYMBOL_OK);
      setText(cardTitle, "Paired");
      setText(cardSub, host);
      break;
    case End::Cancelled:
      setText(cardIconLbl, LV_SYMBOL_CLOSE);
      setText(cardTitle, "Pairing cancelled");
      setText(cardSub, "");
      break;
    case End::None:
      setText(cardIconLbl, "!");
      setText(cardTitle, "Pairing stopped");
      setText(cardSub, "Start again from the PC");
      break;
  }
  fadeOut(content, 150);
  hide(card, false);
  lv_obj_set_style_opa(card, LV_OPA_COVER, 0);
  pop(cardIcon, 380);
}

void create(lv_obj_t* r) {
  root = r;
  content = plain(root);
  lv_obj_set_size(content, LCD_W, LCD_H);

  // the dial: a tick for every number, a longer one every five
  for (int i = 0; i < STEPS; i++) {
    bool major = i % 5 == 0;
    lv_obj_t* t = line(content, tickPts[i], major ? 4 : 3, major ? COL_GREY2 : COL_GREY3);
    float x0, y0, x1, y1;
    polar(i * PER_STEP, major ? 202 : 207, &x0, &y0);
    polar(i * PER_STEP, 220, &x1, &y1);
    setLine(t, tickPts[i], x0, y0, x1, y1);
  }
  marker = circle(content, 16, COL_YELLOW);
  // the time left to finish, round the outside of the dial
  countdown = arc(content, 227, 4, COL_GREY1);

  header(content, "Pair with");
  hostLbl = label(content, &nunito_28, COL_WHITE, "");
  lv_obj_set_width(hostLbl, 290);
  lv_label_set_long_mode(hostLbl, LV_LABEL_LONG_DOT);
  at(hostLbl, CX, 106);

  chipsRow = plain(content);
  lv_obj_set_size(chipsRow, 240, 46);
  at(chipsRow, CX, 160);
  for (int i = 0; i < 3; i++) {
    chip[i] = box(chipsRow, 68, 42, COL_GREY4, LV_RADIUS_CIRCLE);
    lv_obj_set_style_border_width(chip[i], 2, 0);
    lv_obj_set_style_border_opa(chip[i], LV_OPA_COVER, 0);
    center(chip[i], (i - 1) * 80, 0);
    chipLbl[i] = label(chip[i], &nunito_22, COL_GREY1, "");
    center(chipLbl[i], 0, 0);
  }

  valueLbl = label(content, &nunito_digits_110, COL_WHITE, "0");
  at(valueLbl, CX, 252);
  hintLabel(content, "Turn to each number \xC2\xB7 Tap", 336, 330);
  hintLabel(content, "Side button: cancel", HINT_Y, 240);

  card = plain(root);
  lv_obj_set_size(card, LCD_W, LCD_H);
  cardIcon = circle(card, 132, COL_GREEN);
  at(cardIcon, CX, CY - 46);
  cardIconLbl = label(cardIcon, &lv_font_montserrat_48, COL_BG, LV_SYMBOL_OK);
  center(cardIconLbl, 0, 0);
  cardTitle = label(card, &nunito_40, COL_WHITE, "");
  lv_obj_set_width(cardTitle, 360);
  lv_label_set_long_mode(cardTitle, LV_LABEL_LONG_DOT);
  at(cardTitle, CX, CY + 66);
  cardSub = label(card, &nunito_22, COL_GREY1, "");
  lv_obj_set_width(cardSub, 320);
  lv_label_set_long_mode(cardSub, LV_LABEL_LONG_DOT);
  at(cardSub, CX, CY + 110);
  hide(card, true);
}

void enter() { startDialling(); }

void leave() {
  cardAt = 0;
  lv_obj_set_style_opa(content, LV_OPA_COVER, 0);
}

void input(const InputEvent& e) {
  if (cardAt) return;  // the result card leaves on its own
  switch (e.type) {
    case Ev::Turn: {
      int n = detents.feed(e.value, PER_STEP);
      if (!n) break;
      value = ((value + n) % STEPS + STEPS) % STEPS;
      hal::beep(Tone::Tick);
      paintValue();
      break;
    }
    case Ev::Tap:
      if (value == target[stage]) {
        stage++;
        paintChips();
        paintValue();
        hostlink::pairProgress(stage);
        if (stage == 3) {
          // Commit now: the card is only for the eye, and the link may not wait for it
          hal::beep(Tone::Success);
          ending = End::Paired;
          showCard(End::Paired);
          hostlink::pairResult(true);
        } else {
          hal::beep(Tone::Select);
          pop(chip[stage - 1], 240);
        }
      } else {
        hal::beep(Tone::Error);
        shake(chipsRow);
        stage = 0;
        paintChips();
        paintValue();
        hostlink::pairProgress(0);
      }
      break;
    case Ev::Button:
      hal::beep(Tone::Select);
      ending = End::Cancelled;
      showCard(End::Cancelled);
      hostlink::pairResult(false);
      break;
    default:
      break;
  }
}

void update(uint32_t dirty) {
  if (!(dirty & D_PAIR)) return;
  if (g_model.pairing) {
    if (cardAt) {
      startDialling();  // a new pairing right behind the last one
      fadeIn(content, 200);
    } else if (strcmp(host, hostName()) != 0) {
      startDialling();  // another PC took over
    }
    return;
  }
  // Ended by hostlink (the PC went, or time ran out) rather than here
  if (!cardAt) showCard(ending);
}

void tick(uint32_t now) {
  if (cardAt) {
    if ((int32_t)(now - cardAt) < (int32_t)RESULT_MS) return;
    cardAt = 0;
    if (g_model.pairing) startDialling();
    else back(F_PAIR);
    return;
  }
  paintCountdown(now);
}

}  // namespace

const Face FACE_PAIR = {"pair", "Pair", create, enter, leave, input, update, tick};

// For the face simulator (tools/sim), which has to dial the numbers in
int pairTargetForTest(int i) { return i >= 0 && i < 3 ? target[i] : -1; }

}  // namespace ui
