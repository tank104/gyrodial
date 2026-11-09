// Home: a small character that is also the media player. Two tall eyes while something plays
// (they read as a pause sign: tap to pause); closed, resting eyes and a small play mark while it's
// paused; grey eyes and a red mute mark while muted. Turning shows the volume. The clock and
// battery sit at the top, what's playing at the bottom. Swipe for the previous / next track.
#include <string.h>
#include "ui_common.h"

namespace ui {
namespace {

constexpr int EYE_W = 58;
constexpr int EYE_OPEN_H = 124;
constexpr int EYE_SHUT_H = 18;
constexpr int EYE_DX = 64;       // each eye's center from the middle
constexpr int EYE_Y = CY - 22;

lv_obj_t* root;
lv_obj_t* faceBox;  // everything that steps aside while the volume shows
lv_obj_t* eyeL;
lv_obj_t* eyeR;
lv_obj_t* clockLbl;
lv_obj_t* muteIcon;
lv_obj_t* batBox;
lv_obj_t* batBody;
lv_obj_t* batFill;
lv_obj_t* bolt;
lv_obj_t* titleLbl;
lv_obj_t* artistLbl;
lv_obj_t* focusRing;
lv_obj_t* playMark;
VolumeView volView;

bool playing = false;
uint32_t playOverrideAt = 0;
float open = 1.0f;          // 0 = resting, 1 = wide open
float lookX = 0, lookY = 0, lookTX = 0, lookTY = 0;
uint32_t lookReleaseAt = 0;
uint32_t nextBlink = 0, blinkStart = 0, hopStart = 0;
bool doubleBlink = false;
int lastMinute = -1;
int paintedMute = -1;
int drawnH = -1, drawnY = -1, drawnX = 9999;
uint32_t lastFrame = 0;

bool showPlaying() {
  if (playOverrideAt && lv_tick_elaps(playOverrideAt) < 1500) return playing;
  return g_model.playing;
}

void refreshMedia() {
  bool hasTitle = g_model.title[0] != 0;
  if (hasTitle) {
    setText(titleLbl, g_model.title);
    lv_obj_set_style_text_color(titleLbl, rgb(showPlaying() ? COL_WHITE : COL_GREY1), 0);
    setText(artistLbl, g_model.artist[0] ? g_model.artist : g_model.src);
    hide(artistLbl, false);
  } else {
    setText(titleLbl, g_model.src[0] ? g_model.src : "Turn for volume");
    lv_obj_set_style_text_color(titleLbl, rgb(COL_GREY1), 0);
    hide(artistLbl, true);
  }
}

void refreshBattery() {
  int p = g_model.bat >= 0 ? g_model.bat : hal::batteryPercent();
  bool chg = g_model.bat >= 0 ? g_model.chg : hal::charging();
  if (p < 0) {
    hide(batBox, true);
    hide(bolt, true);
    return;
  }
  hide(batBox, false);
  hide(bolt, !chg);
  int w = 2 + 21 * (p > 100 ? 100 : p) / 100;
  lv_obj_set_width(batFill, w);
  uint32_t c = chg ? COL_GREEN : (p < 15 ? COL_RED : COL_WHITE);
  lv_obj_set_style_bg_color(batFill, rgb(c), 0);
  lv_obj_set_style_border_color(batBody, rgb(p < 15 && !chg ? COL_RED : COL_GREY1), 0);
}

void refreshClock() {
  int h, m;
  if (model_local_time(&h, &m, nullptr)) {
    if (m != lastMinute) {
      lv_label_set_text_fmt(clockLbl, "%d:%02d", h, m);
      lastMinute = m;
    }
    hide(clockLbl, false);
  } else {
    hide(clockLbl, true);
  }
}

void refreshMute() {
  bool m = vol::muted();
  if ((int)m == paintedMute) return;
  paintedMute = m;
  hide(muteIcon, !m);
  // muted is a state of the whole face, not a footnote: the eyes go grey
  uint32_t c = m ? COL_GREY2 : COL_YELLOW;
  lv_obj_set_style_bg_color(eyeL, rgb(c), 0);
  lv_obj_set_style_bg_color(eyeR, rgb(c), 0);
}

void hop() { hopStart = lv_tick_get(); }

void create(lv_obj_t* r) {
  root = r;
  faceBox = plain(root);
  lv_obj_set_size(faceBox, LCD_W, LCD_H);

  eyeL = box(faceBox, EYE_W, EYE_OPEN_H, COL_YELLOW, LV_RADIUS_CIRCLE);
  eyeR = box(faceBox, EYE_W, EYE_OPEN_H, COL_YELLOW, LV_RADIUS_CIRCLE);

  // top: mute · clock · battery
  lv_obj_t* row = plain(faceBox);
  lv_obj_set_size(row, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
  lv_obj_set_flex_flow(row, LV_FLEX_FLOW_ROW);
  lv_obj_set_flex_align(row, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
  lv_obj_set_style_pad_column(row, 10, 0);
  at(row, CX, 64);
  muteIcon = label(row, &nunito_28, COL_RED, LV_SYMBOL_MUTE);
  clockLbl = label(row, &nunito_22, COL_GREY1, "");
  batBox = plain(row);
  lv_obj_set_size(batBox, 33, 16);
  batBody = plain(batBox);
  lv_obj_set_size(batBody, 29, 16);
  lv_obj_set_style_radius(batBody, 5, 0);
  lv_obj_set_style_border_width(batBody, 2, 0);
  lv_obj_set_style_border_color(batBody, rgb(COL_GREY1), 0);
  lv_obj_set_style_border_opa(batBody, LV_OPA_COVER, 0);
  batFill = box(batBox, 12, 8, COL_WHITE, 2);
  lv_obj_set_pos(batFill, 4, 4);
  lv_obj_t* nub = box(batBox, 3, 6, COL_GREY1, 1);
  lv_obj_set_pos(nub, 30, 5);
  bolt = label(row, &nunito_18, COL_GREEN, LV_SYMBOL_CHARGE);

  // bottom: what's playing
  titleLbl = label(faceBox, &nunito_22, COL_WHITE, "");
  lv_obj_set_width(titleLbl, 290);
  lv_label_set_long_mode(titleLbl, LV_LABEL_LONG_SCROLL_CIRCULAR);
  at(titleLbl, CX, 352);
  artistLbl = label(faceBox, &nunito_18, COL_GREY1, "");
  lv_obj_set_width(artistLbl, 250);
  lv_label_set_long_mode(artistLbl, LV_LABEL_LONG_DOT);
  at(artistLbl, CX, 382);

  // paused: a small play mark between the resting eyes ("tap to play")
  playMark = label(faceBox, &nunito_28, COL_GREY1, LV_SYMBOL_PLAY);
  at(playMark, CX, EYE_Y + 30);
  hide(playMark, true);

  // the focus timer's time left, round the rim, while it runs on another face
  focusRing = arc(root, 226, 4, COL_YELLOW);
  hide(focusRing, true);

  volView.create(root);
}

void layoutEyes(uint32_t now) {
  float dt = lastFrame ? (int32_t)(now - lastFrame) / 1000.0f : 0.016f;
  if (dt > 0.1f) dt = 0.1f;
  if (dt < 0) dt = 0;
  lastFrame = now;
  bool p = showPlaying();
  open = ease(open, p ? 1.0f : 0.0f, dt, 11.0f);

  float h = EYE_SHUT_H + (EYE_OPEN_H - EYE_SHUT_H) * open;
  float y = EYE_Y + 30.0f * (1.0f - open);

  // blink (only worth doing with the eyes open)
  if ((int32_t)(now - nextBlink) >= 0) {
    blinkStart = now;
    doubleBlink = rndRange(0, 5) == 0;
    nextBlink = now + (uint32_t)rndRange(2200, 6000);
  }
  uint32_t bt = (uint32_t)((int32_t)(now - blinkStart) < 0 ? 0 : now - blinkStart);
  uint32_t blinkLen = doubleBlink ? 380 : 150;
  if (bt < blinkLen && open > 0.5f) {
    float ph = doubleBlink ? fmodf(bt / 190.0f, 1.0f) : bt / 150.0f;
    float b = sinf(ph * PI_F);
    h *= 1.0f - 0.86f * b;
  }

  // a gentle bob while the music plays
  if (open > 0.5f) y += 4.0f * sinf(now * (2.0f * PI_F / 1400.0f)) * open;

  // a hop when tapped
  int32_t ht = (int32_t)(now - hopStart);
  if (hopStart && ht >= 0 && ht < 320) y -= 24.0f * sinf(PI_F * ht / 320.0f);

  // glance toward the finger, drift back after it lifts
  if (lookReleaseAt && (int32_t)(now - lookReleaseAt) >= 0) {
    lookTX = lookTY = 0;
    lookReleaseAt = 0;
  }
  lookX = ease(lookX, lookTX, dt, 14.0f);
  lookY = ease(lookY, lookTY, dt, 14.0f);

  hide(playMark, open > 0.15f);

  int ih = (int)lroundf(h < 10 ? 10 : h);
  int iy = (int)lroundf(y + lookY);
  int ix = (int)lroundf(lookX);
  if (ih == drawnH && iy == drawnY && ix == drawnX) return;
  drawnH = ih;
  drawnY = iy;
  drawnX = ix;
  lv_obj_set_height(eyeL, ih);
  lv_obj_set_height(eyeR, ih);
  at(eyeL, CX - EYE_DX + ix, iy);
  at(eyeR, CX + EYE_DX + ix, iy);
}

void enter() {
  playing = g_model.playing;
  playOverrideAt = 0;
  open = playing ? 1.0f : 0.0f;
  drawnH = -1;
  lastMinute = -1;
  paintedMute = -1;
  lastFrame = 0;
  vol::syncFromModel();
  volView.hideNow();
  hide(faceBox, false);
  lv_obj_set_style_opa(faceBox, LV_OPA_COVER, 0);
  refreshMedia();
  refreshBattery();
  refreshClock();
  refreshMute();
  nextBlink = lv_tick_get() + 1500;
  layoutEyes(lv_tick_get());
}

void input(const InputEvent& e) {
  switch (e.type) {
    case Ev::Turn:
      vol::turn(e.value);
      if (!volView.shown) fadeOut(faceBox, 120);
      volView.show();
      break;
    case Ev::Tap:
      playing = !showPlaying();
      playOverrideAt = lv_tick_get();
      hostlink::sendMedia("toggle");
      hal::beep(Tone::Tap);
      hop();
      refreshMedia();
      break;
    case Ev::Swipe:
      if (e.dir == 'l' || e.dir == 'r') {
        hostlink::sendMedia(e.dir == 'l' ? "prev" : "next");
        hal::beep(Tone::Select);
        lookTX = e.dir == 'l' ? -20.0f : 20.0f;
        lookTY = 0;
        lookReleaseAt = lv_tick_get() + 450;
      }
      break;
    case Ev::LongPress:
      vol::toggleMute();
      if (!volView.shown) fadeOut(faceBox, 120);
      volView.show();
      refreshMute();
      break;
    case Ev::TouchDown:
    case Ev::TouchMove:
      lookTX = clampf((e.x - CX) / (float)CX, -1, 1) * 18.0f;
      lookTY = clampf((e.y - CY) / (float)CY, -1, 1) * 12.0f;
      lookReleaseAt = 0;
      break;
    case Ev::TouchUp:
      lookReleaseAt = lv_tick_get() + 350;
      break;
    default:
      break;
  }
}

void update(uint32_t dirty) {
  if (dirty & (D_MEDIA | D_LINK)) refreshMedia();
  if (dirty & D_AUDIO) {
    refreshMute();
    if (volView.shown) volView.refresh();
  }
  if (dirty & D_BAT) refreshBattery();
  if (dirty & D_TIME) {
    lastMinute = -1;
    refreshClock();
  }
}

void tick(uint32_t now) {
  layoutEyes(now);
  refreshClock();
  bool wasShown = volView.shown;
  if (!volView.tick(now) && wasShown) {
    fadeIn(faceBox, 220);
    refreshMute();
  }
  bool f = focus::active() && !volView.shown;
  hide(focusRing, !f);
  if (f) arcSpan(focusRing, 0, 360.0f * focus::remaining());
}

}  // namespace

const Face FACE_HOME = {"home", "Home", create, enter, nullptr, input, update, tick};

}  // namespace ui
