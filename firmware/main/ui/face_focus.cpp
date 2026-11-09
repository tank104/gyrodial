// Focus: a timer. Turn to set the minutes, tap to start; the ring empties as the session runs. It
// keeps running on the other faces and comes back here, chiming, when it's done.
#include "ui_common.h"

namespace ui {
namespace focus {

enum class St : uint8_t { Set, Running, Paused, Done };

static St s_st = St::Set;
static int s_min = 25;
static uint32_t s_endMs = 0;      // Running: when it ends
static uint32_t s_leftMs = 0;     // Paused: what's left
static uint32_t s_doneAt = 0;

static uint32_t totalMs() { return (uint32_t)s_min * 60000u; }

static uint32_t leftMs(uint32_t now) {
  if (s_st == St::Running) return (int32_t)(s_endMs - now) > 0 ? s_endMs - now : 0;
  if (s_st == St::Paused) return s_leftMs;
  if (s_st == St::Done) return 0;
  return totalMs();
}

bool active() { return s_st == St::Running || s_st == St::Paused; }

float remaining() { return totalMs() ? (float)leftMs(lv_tick_get()) / totalMs() : 0; }

void backgroundTick(uint32_t now) {
  if (s_st != St::Running || (int32_t)(s_endMs - now) > 0) return;
  s_st = St::Done;
  s_doneAt = now;
  hal::beep(Tone::Chime);
  hostlink::sendFocus("done", s_min);
  wake();
  // Like an alarm: come back to say so, unless something more important is up
  FaceId c = current();
  if (c != F_FOCUS && c != F_ALLOW && c != F_PAIR) show(F_FOCUS);
}

}  // namespace focus

namespace {
using focus::St;

lv_obj_t* root;
lv_obj_t* ring;
lv_obj_t* bigNum;    // minutes while setting
lv_obj_t* unit;
lv_obj_t* clock;     // mm:ss while running
lv_obj_t* stateLbl;
lv_obj_t* hint;
Detents detents;
int lastSec = -1;
int lastMin = -1;
St painted = (St)0xFF;

void paint(uint32_t now, bool force) {
  St st = focus::s_st;
  bool changed = force || st != painted;
  painted = st;
  if (changed) {
    hide(bigNum, st != St::Set);
    hide(unit, st != St::Set);
    hide(clock, st == St::Set || st == St::Done);
    hide(stateLbl, st == St::Set || st == St::Running);
    lv_obj_set_style_arc_color(ring, rgb(st == St::Paused ? COL_GREY2 : COL_YELLOW), LV_PART_INDICATOR);
    lv_obj_set_style_text_color(clock, rgb(st == St::Paused ? COL_GREY1 : COL_WHITE), 0);
    switch (st) {
      case St::Set: setText(hint, "Turn \xC2\xB7 Tap to start"); break;
      case St::Running: setText(hint, "Tap: pause \xC2\xB7 Hold: stop"); break;
      case St::Paused:
        setText(stateLbl, "PAUSED");
        lv_obj_set_style_text_color(stateLbl, rgb(COL_GREY1), 0);
        lv_obj_set_style_text_font(stateLbl, &nunito_22, 0);
        at(stateLbl, CX, 132);
        setText(hint, "Tap: resume \xC2\xB7 Hold: stop");
        break;
      case St::Done:
        setText(stateLbl, "Done");
        lv_obj_set_style_text_color(stateLbl, rgb(COL_YELLOW), 0);
        lv_obj_set_style_text_font(stateLbl, &nunito_64, 0);
        at(stateLbl, CX, CY - 10);
        setText(hint, "Tap to finish");
        break;
    }
    lastSec = -1;
  }
  if (st == St::Set) {
    if (focus::s_min != lastMin || changed) {
      lastMin = focus::s_min;
      lv_label_set_text_fmt(bigNum, "%d", focus::s_min);
      arcSpan(ring, 0, 360.0f * focus::s_min / 120.0f);
    }
    return;
  }
  if (st == St::Done) {
    // the ring breathes until it's dismissed
    float t = (int32_t)(now - focus::s_doneAt) / 1000.0f;
    lv_obj_set_style_arc_opa(ring, (lv_opa_t)(150 + 105 * sinf(t * 4.0f)), LV_PART_INDICATOR);
    arcSpan(ring, 0, 360);
    return;
  }
  uint32_t left = focus::leftMs(now);
  int sec = (int)((left + 999) / 1000);
  if (sec != lastSec) {
    lastSec = sec;
    lv_label_set_text_fmt(clock, "%d:%02d", sec / 60, sec % 60);
    lv_obj_set_style_text_font(clock, sec >= 6000 ? &nunito_64 : &nunito_digits_110, 0);
  }
  arcSpan(ring, 360.0f * (1.0f - (float)left / focus::totalMs()), 360);
}

void create(lv_obj_t* r) {
  root = r;
  ring = arc(root, 208, 16, COL_YELLOW);
  arcTrack(ring, COL_GREY4, 0, 360);
  bigNum = label(root, &nunito_digits_150, COL_WHITE, "25");
  at(bigNum, CX, CY - 14);
  unit = label(root, &nunito_22, COL_GREY1, "min");
  at(unit, CX, CY + 72);
  clock = label(root, &nunito_digits_110, COL_WHITE, "25:00");
  at(clock, CX, CY - 4);
  stateLbl = label(root, &nunito_18, COL_GREY1, "PAUSED");
  lv_obj_set_style_text_letter_space(stateLbl, 3, 0);
  // inside the ring, which is narrower down here than the shared hint line allows
  hint = hintLabel(root, "", 368, 270);
  header(root, "Focus");
}

void enter() {
  detents.reset();
  lv_obj_set_style_arc_opa(ring, LV_OPA_COVER, LV_PART_INDICATOR);
  paint(lv_tick_get(), true);
}

void stepMinutes(int n) {
  int m = focus::s_min;
  for (; n > 0; n--) m += m < 30 ? 1 : 5;
  for (; n < 0; n++) m -= m <= 30 ? 1 : 5;
  if (m < 1) m = 1;
  if (m > 120) m = 120;
  if (m != focus::s_min) {
    focus::s_min = m;
    hal::beep(Tone::Tick);
  }
}

void input(const InputEvent& e) {
  uint32_t now = lv_tick_get();
  switch (e.type) {
    case Ev::Turn:
      if (focus::s_st == St::Set) stepMinutes(detents.feed(e.value, 8.0f));
      break;
    case Ev::Tap:
      hal::beep(Tone::Tap);
      switch (focus::s_st) {
        case St::Set:
          focus::s_st = St::Running;
          focus::s_endMs = now + focus::totalMs();
          hostlink::sendFocus("start", focus::s_min);
          rise(clock, 12);
          break;
        case St::Running:
          focus::s_leftMs = focus::leftMs(now);
          focus::s_st = St::Paused;
          hostlink::sendFocus("pause", focus::s_min);
          break;
        case St::Paused:
          focus::s_endMs = now + focus::s_leftMs;
          focus::s_st = St::Running;
          hostlink::sendFocus("start", focus::s_min);
          break;
        case St::Done:
          focus::s_st = St::Set;
          lv_obj_set_style_arc_opa(ring, LV_OPA_COVER, LV_PART_INDICATOR);
          break;
      }
      break;
    case Ev::LongPress:
      if (focus::active()) {
        focus::s_st = St::Set;
        hostlink::sendFocus("reset", focus::s_min);
        hal::beep(Tone::Select);
        lv_obj_set_style_arc_opa(ring, LV_OPA_COVER, LV_PART_INDICATOR);
      }
      break;
    default:
      break;
  }
  paint(now, false);
}

void tick(uint32_t now) { paint(now, false); }

}  // namespace

const Face FACE_FOCUS = {"focus", "Focus", create, enter, nullptr, input, nullptr, tick};

}  // namespace ui
