// Game: a dialkit-watt game page has the puck as its controller. The paddle on the rim is where the
// page says it is; turning moves it (and tells the page), taps and swipes go straight through.
#include "ui_common.h"

namespace ui {
namespace {

constexpr float PADDLE_SPAN = 44.0f;

lv_obj_t* root;
lv_obj_t* track;
lv_obj_t* paddle;
lv_obj_t* labelLbl;
lv_obj_t* hint;
lv_obj_t* flash;
float angle = 0;           // where the paddle is drawn (clockwise from 12)
uint32_t localAt = 0;      // a local turn wins over the page's angle for a moment

void drawPaddle() { arcSpan(paddle, angle - PADDLE_SPAN / 2, angle + PADDLE_SPAN / 2); }

void create(lv_obj_t* r) {
  root = r;
  track = arc(root, 212, 4, COL_GREY4);
  arcSpan(track, 0, 360);
  paddle = arc(root, 212, 18, COL_YELLOW);
  labelLbl = label(root, &nunito_40, COL_WHITE, "");
  lv_obj_set_width(labelLbl, 300);
  lv_label_set_long_mode(labelLbl, LV_LABEL_LONG_DOT);
  at(labelLbl, CX, CY - 12);
  hint = hintLabel(root, "Turn \xC2\xB7 Tap", CY + 44, 260);
  flash = ringOutline(root, 120, 4, COL_YELLOW);
  at(flash, CX, CY);
  hide(flash, true);
}

void enter() {
  angle = g_model.gameAngle;
  localAt = 0;
  setText(labelLbl, g_model.gameLabel[0] ? g_model.gameLabel : "GAME");
  drawPaddle();
}

void ping() {
  hide(flash, false);
  lv_obj_set_style_opa(flash, LV_OPA_COVER, 0);
  pop(flash, 240);
  fadeOut(flash, 400);
}

void input(const InputEvent& e) {
  switch (e.type) {
    case Ev::Turn:
      hostlink::sendGameTurn(e.value);
      angle = fmodf(angle + e.value + 360.0f, 360.0f);
      localAt = lv_tick_get();
      drawPaddle();
      break;
    case Ev::Tap:
      hostlink::sendGame("tap");
      hal::beep(Tone::Tap);
      ping();
      break;
    case Ev::LongPress:
      hostlink::sendGame("hold");
      hal::beep(Tone::Select);
      break;
    case Ev::Swipe: {
      char v[2] = {e.dir, 0};
      hostlink::sendGame("swipe", v);
      break;
    }
    default:
      break;
  }
}

void update(uint32_t dirty) {
  if (!(dirty & D_GAME)) return;
  setText(labelLbl, g_model.gameLabel[0] ? g_model.gameLabel : "GAME");
  if (!localAt || lv_tick_elaps(localAt) > 300) {
    angle = g_model.gameAngle;
    drawPaddle();
  }
}

}  // namespace

const Face FACE_GAME = {"game", "Game", create, enter, nullptr, input, update, nullptr};

}  // namespace ui
