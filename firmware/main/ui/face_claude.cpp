// Claude: tap, talk, tap to send. The words are recognized on the PC; the dial shows the spark, the
// live transcript, and then the answer (turn or swipe to scroll it). Hold to cancel.
#include <stdio.h>
#include <string.h>
#include "ui_common.h"

namespace ui {

// ---------------------------------------------------------------- the spark
// Eight rounded rays, long and short in turn: our own mark for "Claude is here".

void Spark::create(lv_obj_t* parent, uint32_t c) {
  for (int i = 0; i < 8; i++) rays[i] = line(parent, pts[i], 8, c);
}

void Spark::color(uint32_t c) {
  for (auto* r : rays) lv_obj_set_style_line_color(r, rgb(c), 0);
}

void Spark::draw(float cx, float cy, float size, float rotDeg, float breathe) {
  float w = size * 0.2f;
  if (w < 3) w = 3;
  lv_coord_t iw = (lv_coord_t)lroundf(w);
  float t = lv_tick_get() / 1000.0f;
  for (int i = 0; i < 8; i++) {
    float len = (i % 2 == 0 ? 1.0f : 0.7f) * size;
    if (breathe > 0) len *= 1.0f + breathe * 0.2f * sinf(t * 5.0f - i * 0.785f);
    float r0 = 0;
    float x0, y0, x1, y1;
    polar(rotDeg + i * 45.0f, r0, &x0, &y0, 0, 0);
    polar(rotDeg + i * 45.0f, len, &x1, &y1, 0, 0);
    if (lv_obj_get_style_line_width(rays[i], 0) != iw) lv_obj_set_style_line_width(rays[i], iw, 0);
    setLine(rays[i], pts[i], cx + x0, cy + y0, cx + x1, cy + y1);
  }
}

// ---------------------------------------------------------------- the face
namespace {

lv_obj_t* root;
Spark spark;
lv_obj_t* ring1;
lv_obj_t* ring2;
lv_obj_t* status;
lv_obj_t* transcript;
Scroller reply;
lv_obj_t* hint;

ClaudeState local = ClaudeState::Idle;
uint32_t localAt = 0;
ClaudeState shownState = (ClaudeState)0xFF;
bool shownLinked = false;
float sx = CX, sy = CY - 20, ss = 78, srot = 0;
uint32_t lastFrame = 0;
char tail[200];

ClaudeState state() {
  if (localAt && lv_tick_elaps(localAt) < 3000) return local;
  return g_model.claude;
}

void setLocal(ClaudeState s) {
  local = s;
  localAt = lv_tick_get();
}

const char* transcriptTail(const char* text) {
  size_t n = strlen(text);
  if (n <= 150) return text;
  const char* p = text + n - 150;
  while (*p && *p != ' ') p++;
  snprintf(tail, sizeof tail, "\xE2\x80\xA6%s", p);  // …
  return tail;
}

void layoutState(bool force) {
  ClaudeState s = state();
  bool linked = g_model.linked;
  if (!force && s == shownState && linked == shownLinked) return;
  shownState = s;
  shownLinked = linked;

  hide(ring1, s != ClaudeState::Listening);
  hide(ring2, s != ClaudeState::Listening);
  hide(transcript, s != ClaudeState::Listening);
  hide(reply.box, s != ClaudeState::Reply);
  if (s != ClaudeState::Reply) {
    hide(reply.track, true);
    hide(reply.thumb, true);
  }
  hide(status, s == ClaudeState::Listening || s == ClaudeState::Reply);
  spark.color(s == ClaudeState::Error ? COL_GREY2 : COL_CLAUDE);
  lv_obj_set_style_text_color(status, rgb(s == ClaudeState::Error ? COL_RED : COL_GREY1), 0);

  switch (s) {
    case ClaudeState::Idle:
      setText(status, linked ? "Tap to talk" : "Connect Gyrodial to your PC");
      setText(hint, "");
      break;
    case ClaudeState::Listening:
      setText(hint, "Tap: send \xC2\xB7 Hold: cancel");
      fadeIn(transcript, 200);
      break;
    case ClaudeState::Thinking:
      setText(status, "Thinking\xE2\x80\xA6");
      setText(hint, "Hold to cancel");
      break;
    case ClaudeState::Reply:
      setText(hint, "Tap to reply");
      reply.toTop();
      reply.refreshCue();
      fadeIn(reply.box, 220);
      break;
    case ClaudeState::Error:
      setText(status, g_model.claudeText[0] ? g_model.claudeText : "Something went wrong");
      setText(hint, "Tap to try again");
      break;
  }
}

void refreshText() {
  ClaudeState s = state();
  if (s == ClaudeState::Listening) {
    bool any = g_model.claudeText[0] && g_model.claude == ClaudeState::Listening;
    setText(transcript, any ? transcriptTail(g_model.claudeText) : "Listening\xE2\x80\xA6");
    lv_obj_set_style_text_color(transcript, rgb(any ? COL_WHITE : COL_GREY1), 0);
  } else if (s == ClaudeState::Reply) {
    reply.setText(g_model.claudeText);
    reply.refreshCue();
  } else if (s == ClaudeState::Error) {
    setText(status, g_model.claudeText[0] ? g_model.claudeText : "Something went wrong");
  }
}

void create(lv_obj_t* r) {
  root = r;
  ring1 = ringOutline(root, 100, 3, COL_CLAUDE);
  ring2 = ringOutline(root, 140, 2, COL_CLAUDE);
  lv_obj_set_style_border_opa(ring2, LV_OPA_40, 0);
  spark.create(root, COL_CLAUDE);

  status = label(root, &nunito_22, COL_GREY1, "Tap to talk");
  lv_obj_set_width(status, 320);
  lv_label_set_long_mode(status, LV_LABEL_LONG_WRAP);
  at(status, CX, 350);

  transcript = label(root, &nunito_22, COL_WHITE, "");
  lv_obj_set_width(transcript, 330);
  lv_label_set_long_mode(transcript, LV_LABEL_LONG_WRAP);
  lv_obj_set_style_text_line_space(transcript, 4, 0);
  at(transcript, CX, 262);

  reply.create(root, 344, 250, CX, 236, &nunito_22, COL_WHITE, 5);
  hide(reply.box, true);

  hint = hintLabel(root, "");
}

void enter() {
  localAt = 0;
  lastFrame = 0;
  shownState = (ClaudeState)0xFF;
  layoutState(true);
  refreshText();
  ClaudeState s = state();
  sx = CX;
  sy = s == ClaudeState::Listening ? 120 : (s == ClaudeState::Reply ? 84 : CY - 30);
  ss = s == ClaudeState::Listening ? 40 : (s == ClaudeState::Reply ? 24 : 78);
}

void input(const InputEvent& e) {
  ClaudeState s = state();
  switch (e.type) {
    case Ev::Tap:
      if (!g_model.linked) {
        shake(status);
        hal::beep(Tone::Error);
        break;
      }
      if (s == ClaudeState::Listening) {
        hostlink::sendClaude("send");
        setLocal(ClaudeState::Thinking);
      } else if (s != ClaudeState::Thinking) {
        hostlink::sendClaude("listen");
        setLocal(ClaudeState::Listening);
      }
      hal::beep(Tone::Tap);
      layoutState(false);
      refreshText();
      break;
    case Ev::LongPress:
      hostlink::sendClaude("cancel");
      setLocal(ClaudeState::Idle);
      hal::beep(Tone::Select);
      layoutState(false);
      break;
    case Ev::Turn:
      if (s == ClaudeState::Reply) reply.scrollBy((int)lroundf(e.value * 3.0f), false);
      break;
    case Ev::Swipe:
      // a swipe up moves the text up, to what's below
      if (s == ClaudeState::Reply && (e.dir == 'u' || e.dir == 'd')) reply.scrollBy(e.dir == 'u' ? 84 : -84);
      break;
    default:
      break;
  }
}

void update(uint32_t dirty) {
  if (dirty & D_CLAUDE) {
    localAt = 0;
    layoutState(false);
    refreshText();
  }
  if (dirty & D_LINK) layoutState(true);
}

void tick(uint32_t now) {
  if (localAt && lv_tick_elaps(localAt) >= 3000) {
    localAt = 0;
    layoutState(false);
    refreshText();
  }
  ClaudeState s = state();
  float t = now / 1000.0f;
  float dt = lastFrame ? (int32_t)(now - lastFrame) / 1000.0f : 0.016f;
  if (dt > 0.1f) dt = 0.1f;
  if (dt < 0) dt = 0;
  lastFrame = now;
  float tx = CX, ty, ts, breathe = 0, spin = 0;  // spin in degrees per second
  switch (s) {
    case ClaudeState::Listening: ty = 120; ts = 40; breathe = 0.6f; spin = 30; break;
    case ClaudeState::Thinking: ty = CY - 30; ts = 72; breathe = 0.9f; spin = 200; break;
    case ClaudeState::Reply: ty = 84; ts = 24; break;
    case ClaudeState::Error: ty = CY - 40; ts = 60; break;
    default: ty = CY - 30; ts = 78 + 4.0f * sinf(t * 2.0f); spin = 12; break;
  }
  srot = fmodf(srot + spin * dt, 360.0f);
  sx = ease(sx, tx, dt, 12.0f);
  sy = ease(sy, ty, dt, 12.0f);
  ss = ease(ss, ts, dt, 12.0f);
  spark.draw(sx, sy, ss, srot, breathe);

  if (s == ClaudeState::Listening) {
    float lvl = g_model.claudeLvl > 0 ? g_model.claudeLvl : 0.35f + 0.35f * sinf(t * 3.1f);
    int d1 = (int)(96 + lvl * 56), d2 = (int)(d1 + 34 + 10 * sinf(t * 2.3f));
    lv_obj_set_size(ring1, d1, d1);
    lv_obj_set_size(ring2, d2, d2);
    at(ring1, (int)sx, (int)sy);
    at(ring2, (int)sx, (int)sy);
  }
}

}  // namespace

const Face FACE_CLAUDE = {"claude", "Claude", create, enter, nullptr, input, update, tick};

}  // namespace ui
