// The face manager: which face is up, which ones the app brings up on its own (and where they go
// back to), the picker and overlays, and when the screen dims and sleeps.
#include "../ui.h"
#include <stdio.h>
#include <string.h>
#include "ui_common.h"

namespace ui {

static const Face* const FACES[F_COUNT] = {
    &FACE_HOME, &FACE_TRACKPAD, &FACE_CLAUDE, &FACE_ALLOW, &FACE_DIALKIT, &FACE_GAME,
    &FACE_CALL, &FACE_FOCUS,    &FACE_SPIN,   &FACE_DICE,  &FACE_PONG,    &FACE_PAIR,
};

static lv_obj_t* s_root[F_COUNT];
static FaceId s_cur = F_NONE;
static FaceId s_modalReturn = F_HOME;    // where allow / pair go back to
static FaceId s_contextReturn = F_HOME;  // where dialkit / game / call go back to
static bool s_autoPong = false;          // pong came up because the PC went away
static char s_reported[16] = "";
// Context faces the person backed out of with the side button: they don't pop up again until
// the PC turns them off and on (a DialKit page sends updates all the time)
static bool s_dismissed[F_COUNT] = {};
static bool s_lowBatWarned = false;

// screen power
static uint32_t s_wakeMs = 0;            // last time something other than the hal counted as activity
static bool s_dimmed = false;
static int s_brightApplied = -1;

static bool isModal(FaceId f) { return f == F_ALLOW || f == F_PAIR; }
static bool isContext(FaceId f) { return f == F_DIALKIT || f == F_GAME || f == F_CALL; }
static bool needsPc(FaceId f) {
  return f == F_HOME || f == F_TRACKPAD || f == F_CLAUDE || f == F_DIALKIT || f == F_GAME || f == F_CALL ||
         f == F_ALLOW;
}

FaceId current() { return s_cur; }
const Face& face(FaceId f) { return *FACES[f]; }

void reportFace(const char* id) {
  if (strncmp(s_reported, id, sizeof s_reported) == 0) return;
  strncpy(s_reported, id, sizeof s_reported - 1);
  s_reported[sizeof s_reported - 1] = 0;
  hostlink::sendFace(id);
}

void restoreInputMode() {
  hal::setInputMode(s_cur == F_TRACKPAD && !picker::isOpen() && !overlay::powerOpen() ? InputMode::Trackpad
                                                                                       : InputMode::Ui);
}

void show(FaceId f) {
  if (f >= F_COUNT || f == s_cur) return;
  FaceId prev = s_cur;
  if (prev != F_NONE) {
    if (FACES[prev]->leave) FACES[prev]->leave();
    lv_obj_add_flag(s_root[prev], LV_OBJ_FLAG_HIDDEN);
  }
  s_cur = f;
  lv_obj_t* r = s_root[f];
  lv_obj_clear_flag(r, LV_OBJ_FLAG_HIDDEN);
  reportFace(FACES[f]->id);
  if (FACES[f]->enter) FACES[f]->enter();
  // Arrive: a quick fade and a small rise, so a change of face reads as a change of mode
  if (prev != F_NONE) {
    fadeIn(r, 200);
    rise(r, 14, 260);
  }
}

static void showModal(FaceId f) {
  if (s_cur == f) return;
  if (!isModal(s_cur) && s_cur != F_NONE) s_modalReturn = s_cur;
  show(f);
}

static void showContext(FaceId f) {
  if (s_cur == f) return;
  if (!isContext(s_cur) && !isModal(s_cur) && s_cur != F_NONE) s_contextReturn = s_cur;
  show(f);
}

static bool stillWanted(FaceId f) {
  switch (f) {
    case F_DIALKIT: return g_model.dialOn;
    case F_GAME: return g_model.gameOn;
    case F_CALL: return g_model.callOn;
    case F_ALLOW: return g_model.askActive;
    case F_PAIR: return g_model.pairing;
    default: return f < F_COUNT;
  }
}

void back(FaceId from) {
  if (s_cur != from) return;
  FaceId to = isModal(from) ? s_modalReturn : s_contextReturn;
  if (to == from || !stillWanted(to)) to = s_contextReturn;  // e.g. allow, over a DialKit page that has gone
  if (to == from || !stillWanted(to)) to = F_HOME;
  if (!g_model.linked && needsPc(to)) {
    to = F_PONG;
    s_autoPong = true;
  } else if (to == F_PONG && s_autoPong && g_model.linked) {
    // Pong was only standing in while there was no PC (e.g. pairing over the waiting screen)
    to = F_HOME;
    s_autoPong = false;
  }
  show(to);
}

// ---- screen power

static void applyBrightness(int pct) {
  if (pct == s_brightApplied) return;
  s_brightApplied = pct;
  hal::setBrightness((uint8_t)pct);
}

void wake() {
  s_wakeMs = lv_tick_get();
  if (!hal::displayOn()) hal::setDisplayOn(true);
  s_dimmed = false;
  applyBrightness(g_model.bright);
}

static bool keepAwake() {
  if (s_cur == F_ALLOW || s_cur == F_PAIR) return true;
  if (s_cur == F_CLAUDE && (g_model.claude == ClaudeState::Listening || g_model.claude == ClaudeState::Thinking))
    return true;
  return false;
}

static void powerPolicy(uint32_t now) {
  if (!hal::displayOn()) return;  // stays off until hal reports a Wake
  uint32_t last = hal::lastActivityMs();
  if ((int32_t)(s_wakeMs - last) > 0) last = s_wakeMs;
  // Signed: the input task can note activity after `now` was read (and waking the panel stamps
  // activity ~150 ms ahead), which as unsigned would read as 49 days idle and blank the screen.
  int32_t sinceActivity = (int32_t)(now - last);
  uint32_t idle = sinceActivity > 0 ? (uint32_t)sinceActivity : 0;
  if (keepAwake()) idle = 0;
  if (g_model.offSec > 0 && idle > (uint32_t)g_model.offSec * 1000u) {
    hal::setDisplayOn(false);
    return;
  }
  if (g_model.dimSec > 0 && idle > (uint32_t)g_model.dimSec * 1000u) {
    int dim = g_model.bright / 4;
    if (dim < 4) dim = 4;
    s_dimmed = true;
    applyBrightness(dim);
  } else {
    s_dimmed = false;
    applyBrightness(g_model.bright);
  }
}

// ---- the link coming and going

static void linkChanged() {
  // The pair face leaves on its own once pairing is over (after its result card), and back()
  // then takes it to pong or home as the link stands
  if (s_cur == F_PAIR) return;
  if (!g_model.linked) {
    if (needsPc(s_cur)) {
      show(F_PONG);
      s_autoPong = true;
    }
  } else if (s_cur == F_PONG && s_autoPong) {
    s_autoPong = false;
    show(F_HOME);
  }
}

static FaceId byId(const char* id) {
  for (int i = 0; i < F_COUNT; i++)
    if (strcmp(FACES[i]->id, id) == 0) return (FaceId)i;
  if (strcmp(id, "pointer") == 0) return F_TRACKPAD;
  return F_NONE;
}

}  // namespace ui

// ---------------------------------------------------------------- ui.h

namespace ui {

void begin() {
  lv_obj_t* scr = lv_scr_act();
  lv_obj_remove_style_all(scr);
  lv_obj_set_style_bg_color(scr, lv_color_black(), 0);
  lv_obj_set_style_bg_opa(scr, LV_OPA_COVER, 0);
  lv_obj_clear_flag(scr, LV_OBJ_FLAG_SCROLLABLE);

  for (int i = 0; i < F_COUNT; i++) {
    lv_obj_t* r = plain(scr);
    lv_obj_set_size(r, LCD_W, LCD_H);
    lv_obj_set_pos(r, 0, 0);
    lv_obj_add_flag(r, LV_OBJ_FLAG_HIDDEN);
    s_root[i] = r;
    FACES[i]->create(r);
  }
  overlay::create();

  s_wakeMs = lv_tick_get();
  applyBrightness(g_model.bright);
  // Nothing to talk to yet: play pong until the PC shows up
  if (g_model.linked) {
    show(F_HOME);
  } else {
    show(F_PONG);
    s_autoPong = true;
  }
}

void handleInput(const InputEvent& e) {
  // Anything the hal hands us is a person: the screen comes back to full brightness
  if (e.type == Ev::Wake || !hal::displayOn()) {
    wake();
    if (e.type == Ev::Wake) return;
  } else if (s_dimmed) {
    wake();
  }

  if (overlay::powerOpen()) {
    overlay::powerInput(e);
    return;
  }
  if (picker::isOpen()) {
    picker::input(e);
    return;
  }

  if (e.type == Ev::ButtonLong) {
    overlay::powerConfirm();
    return;
  }
  // The side button: "back" on the faces the PC brought up, the picker everywhere else (the pair
  // face takes it as cancel; the allow face ignores it so it can't be mistaken for an answer)
  if (e.type == Ev::Button && isContext(s_cur)) {
    hal::beep(Tone::Select);
    s_dismissed[s_cur] = true;
    FaceId from = s_cur;
    back(from);
    return;
  }
  if (e.type == Ev::Button && s_cur != F_ALLOW && s_cur != F_PAIR) {
    picker::open();
    return;
  }
  if (s_cur != F_NONE && FACES[s_cur]->input) FACES[s_cur]->input(e);
}

void update(uint32_t dirty) {
  if (dirty & D_CFG) {
    if (!s_dimmed && hal::displayOn()) applyBrightness(g_model.bright);
  }
  if (dirty & D_AUDIO) vol::syncFromModel();
  if (dirty & D_LINK) linkChanged();

  // Pairing ending is the pair face's to show (it puts up a result card, then goes back itself)
  if ((dirty & D_PAIR) && g_model.pairing && s_cur != F_PAIR) {
    wake();
    picker::close();
    showModal(F_PAIR);
  }
  if ((dirty & D_ASK) && g_model.askActive && s_cur != F_PAIR && !askAnswered()) {
    wake();
    picker::close();
    showModal(F_ALLOW);
  }
  if (dirty & D_DIAL) {
    if (!g_model.dialOn) s_dismissed[F_DIALKIT] = false;
    if (g_model.dialOn && s_cur == F_HOME && !s_dismissed[F_DIALKIT]) showContext(F_DIALKIT);
    else if (!g_model.dialOn) back(F_DIALKIT);
  }
  if (dirty & D_GAME) {
    if (!g_model.gameOn) s_dismissed[F_GAME] = false;
    if (g_model.gameOn && s_cur == F_HOME && !s_dismissed[F_GAME]) showContext(F_GAME);
    else if (!g_model.gameOn) back(F_GAME);
  }
  if (dirty & D_CALL) {
    if (!g_model.callOn) s_dismissed[F_CALL] = false;
    if (g_model.callOn && s_cur == F_HOME && !s_dismissed[F_CALL]) {
      wake();
      showContext(F_CALL);
    } else if (!g_model.callOn) {
      back(F_CALL);
    }
  }
  if (dirty & D_BAT) {
    // Once, as it crosses 10 % on battery; again only after it has been charged past 15 %
    if (g_model.bat >= 0 && g_model.bat <= 10 && !g_model.chg && !s_lowBatWarned) {
      s_lowBatWarned = true;
      char msg[40];
      snprintf(msg, sizeof msg, "Battery low \xC2\xB7 %d%%", g_model.bat);
      overlay::banner(msg, false);
    } else if (g_model.chg || g_model.bat > 15) {
      s_lowBatWarned = false;
    }
  }
  if (dirty & D_NOTIFY) {
    if (g_model.notifyText[0]) {
      wake();
      overlay::banner(g_model.notifyText, g_model.notifyKind == NotifyKind::Claude);
    }
  }
  if ((dirty & D_FACE_REQ) && g_model.faceRequest[0]) {
    FaceId f = byId(g_model.faceRequest);
    g_model.faceRequest[0] = 0;
    if (f != F_NONE && !isModal(s_cur) && !isModal(f) && stillWanted(f)) {
      wake();
      picker::close();
      s_autoPong = false;
      show(f);
    }
  }

  if (s_cur != F_NONE && FACES[s_cur]->update) FACES[s_cur]->update(dirty);
}

void tick(uint32_t now) {
  focus::backgroundTick(now);
  if (s_cur != F_NONE && FACES[s_cur]->tick) FACES[s_cur]->tick(now);
  picker::tick(now);
  overlay::tick(now);
  powerPolicy(now);
}

const char* currentFace() {
  if (s_reported[0]) return s_reported;
  return s_cur == F_NONE ? "home" : FACES[s_cur]->id;
}

// The picker picked: a person chose this face, so pong is no longer standing in for the PC
void pickerChose(FaceId f) {
  s_autoPong = false;
  if (f < F_COUNT) s_dismissed[f] = false;
  if (f != s_cur) show(f);
  restoreInputMode();
}

}  // namespace ui
