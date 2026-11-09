// Call: you're in a Teams meeting. Tap to mute or unmute; turning still sets the volume.
#include "ui_common.h"

namespace ui {

// ---------------------------------------------------------------- the microphone glyph

void Mic::create(lv_obj_t* parent, float s) {
  int w = (int)(140 * s), h = (int)(200 * s);
  root = plain(parent);
  lv_obj_set_size(root, w, h);
  float capCy = -44 * s, arcCy = -30 * s, arcR = 54 * s;
  capsule = box(root, (int)(64 * s), (int)(112 * s), COL_WHITE, LV_RADIUS_CIRCLE);
  center(capsule, 0, (int)capCy);
  cradle = arc(root, (int)arcR, (int)(10 * s), COL_WHITE);
  center(cradle, 0, (int)arcCy);
  arcSpan(cradle, 90, 270);
  stem = box(root, (int)(10 * s), (int)(26 * s), COL_WHITE);
  center(stem, 0, (int)(arcCy + arcR + 13 * s));
  base = box(root, (int)(64 * s), (int)(10 * s), COL_WHITE, (int)(5 * s));
  center(base, 0, (int)(arcCy + arcR + 28 * s));
  slashUnder = line(root, slashUnderPts, (int)(22 * s), COL_BG);
  slash = line(root, slashPts, (int)(10 * s), COL_RED);
  float cx = w / 2.0f, cy = h / 2.0f;
  setLine(slashUnder, slashUnderPts, cx - 56 * s, cy - 82 * s, cx + 56 * s, cy + 66 * s);
  setLine(slash, slashPts, cx - 56 * s, cy - 82 * s, cx + 56 * s, cy + 66 * s);
  set(false);
}

void Mic::set(bool muted) {
  uint32_t c = muted ? COL_RED : COL_WHITE;
  lv_obj_set_style_bg_color(capsule, rgb(c), 0);
  lv_obj_set_style_arc_color(cradle, rgb(c), LV_PART_INDICATOR);
  lv_obj_set_style_bg_color(stem, rgb(c), 0);
  lv_obj_set_style_bg_color(base, rgb(c), 0);
  hide(slashUnder, !muted);
  hide(slash, !muted);
}

// ---------------------------------------------------------------- the face
namespace {

lv_obj_t* root;
lv_obj_t* content;
lv_obj_t* backdrop;
lv_obj_t* appLbl;
lv_obj_t* statusLbl;
lv_obj_t* hint;
Mic mic;
VolumeView volView;
bool localMuted = false;
uint32_t localAt = 0;
int painted = -1;

bool muted() { return localAt && lv_tick_elaps(localAt) < 1500 ? localMuted : g_model.callMuted; }

void paint(bool force) {
  bool m = muted();
  if (!force && (int)m == painted) return;
  painted = m;
  mic.set(m);
  lv_obj_set_style_bg_color(backdrop, rgb(m ? COL_RED : COL_GREY4), 0);
  lv_obj_set_style_bg_opa(backdrop, m ? LV_OPA_20 : LV_OPA_COVER, 0);
  setText(statusLbl, m ? "Muted" : "Mic on");
  lv_obj_set_style_text_color(statusLbl, rgb(m ? COL_RED : COL_WHITE), 0);
  setText(hint, m ? "Tap to unmute" : "Tap to mute");
}

void create(lv_obj_t* r) {
  root = r;
  content = plain(root);
  lv_obj_set_size(content, LCD_W, LCD_H);
  backdrop = circle(content, 236, COL_GREY4);
  at(backdrop, CX, CY - 22);
  mic.create(content, 1.0f);
  at(mic.root, CX, CY - 22);
  appLbl = header(content, "Teams");
  statusLbl = label(content, &nunito_28, COL_WHITE, "Mic on");
  at(statusLbl, CX, 334);
  hint = hintLabel(content, "Tap to mute");
  volView.create(root);
}

void enter() {
  localAt = 0;
  setHeader(appLbl, g_model.callApp[0] ? g_model.callApp : "Meeting");
  volView.hideNow();
  hide(content, false);
  lv_obj_set_style_opa(content, LV_OPA_COVER, 0);
  paint(true);
}

void input(const InputEvent& e) {
  switch (e.type) {
    case Ev::Tap:
      localMuted = !muted();
      localAt = lv_tick_get();
      hostlink::sendCall("mute");
      hal::beep(Tone::Tap);
      paint(false);
      rise(mic.root, -12, 260);
      break;
    case Ev::Turn:
      vol::turn(e.value);
      if (!volView.shown) fadeOut(content, 120);
      volView.show();
      break;
    default:
      break;
  }
}

void update(uint32_t dirty) {
  if (dirty & D_CALL) {
    localAt = 0;
    paint(false);
  }
  if ((dirty & D_AUDIO) && volView.shown) volView.refresh();
}

void tick(uint32_t now) {
  bool wasShown = volView.shown;
  if (!volView.tick(now) && wasShown) fadeIn(content, 220);
  if (localAt && lv_tick_elaps(localAt) >= 1500) {
    localAt = 0;
    paint(false);
  }
}

}  // namespace

const Face FACE_CALL = {"call", "Call", create, enter, nullptr, input, update, tick};

}  // namespace ui
