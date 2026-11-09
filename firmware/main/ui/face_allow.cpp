// Allow: Claude Code is asking for permission. Nothing is chosen at first, so a stray tap can't
// answer: turn clockwise for Allow or anticlockwise for Deny, then tap. Swipe up and down to read
// a long command.
#include <string.h>
#include "ui_common.h"

namespace ui {
namespace {

enum class Choice : uint8_t { None, Deny, Allow };

lv_obj_t* root;
lv_obj_t* content;
lv_obj_t* proj;
lv_obj_t* tool;
Scroller detail;
lv_obj_t* pills;
lv_obj_t* denyPill;
lv_obj_t* denyLbl;
lv_obj_t* allowPill;
lv_obj_t* allowLbl;
lv_obj_t* hint;
lv_obj_t* result;
lv_obj_t* resultIcon;

Choice choice = Choice::None;
Detents turn;
uint32_t shownAt = 0;
uint32_t answeredAt = 0;   // non-zero while the ✓ / ✗ shows
char askId[32] = "";
char answeredId[32] = "";  // so a late repeat of the same request doesn't pop up again

void paintPill(lv_obj_t* pill, lv_obj_t* lbl, bool on, uint32_t color) {
  lv_obj_set_style_bg_opa(pill, on ? LV_OPA_COVER : LV_OPA_TRANSP, 0);
  lv_obj_set_style_border_color(pill, rgb(on ? color : COL_GREY2), 0);
  lv_obj_set_style_text_color(lbl, rgb(on ? COL_BG : COL_GREY1), 0);
}

void paintChoice() {
  paintPill(denyPill, denyLbl, choice == Choice::Deny, COL_RED);
  paintPill(allowPill, allowLbl, choice == Choice::Allow, COL_GREEN);
  switch (choice) {
    case Choice::None: setText(hint, "Turn to choose"); break;
    case Choice::Deny: setText(hint, "Tap to deny"); break;
    case Choice::Allow: setText(hint, "Tap to allow"); break;
  }
}

lv_obj_t* pill(lv_obj_t* parent, const char* text, uint32_t color, lv_obj_t** lbl) {
  lv_obj_t* p = box(parent, 132, 62, color, LV_RADIUS_CIRCLE);
  lv_obj_set_style_border_width(p, 3, 0);
  lv_obj_set_style_border_opa(p, LV_OPA_COVER, 0);
  *lbl = label(p, &nunito_28, COL_BG, text);
  center(*lbl, 0, 0);
  return p;
}

void load() {
  strncpy(askId, g_model.askId, sizeof askId - 1);
  askId[sizeof askId - 1] = 0;
  setText(proj, g_model.askProj);
  hide(proj, g_model.askProj[0] == 0);
  const char* t = g_model.askTool[0] ? g_model.askTool : "Permission";
  setText(tool, t);
  // one line sits just above the detail; two (an MCP tool) take the room down to it
  lv_point_t sz;
  lv_txt_get_size(&sz, t, &nunito_28, 0, 0, 330, LV_TEXT_FLAG_NONE);
  bool two = sz.y > 44;
  lv_obj_set_height(tool, two ? 72 : 38);
  at(tool, CX, two ? 152 : 160);
  detail.setText(g_model.askText);
  detail.toTop();
  detail.refreshCue();
  choice = Choice::None;
  turn.reset();
  paintChoice();
  shownAt = lv_tick_get();
  answeredAt = 0;
  hide(content, false);
  hide(result, true);
}

void create(lv_obj_t* r) {
  root = r;
  content = plain(root);
  lv_obj_set_size(content, LCD_W, LCD_H);

  header(content, "Claude Code", COL_CLAUDE);
  proj = label(content, &nunito_22, COL_GREY1, "");
  lv_obj_set_width(proj, 280);
  lv_label_set_long_mode(proj, LV_LABEL_LONG_DOT);
  at(proj, CX, 100);

  // the tool, up to two lines (MCP tools have long names), broken between words
  tool = label(content, &nunito_28, COL_WHITE, "");
  lv_obj_set_size(tool, 330, 72);
  lv_label_set_long_mode(tool, LV_LABEL_LONG_DOT);
  lv_obj_set_style_text_line_space(tool, 0, 0);
  at(tool, CX, 152);

  detail.create(content, 340, 116, CX, 248, &nunito_22, COL_GREY1, 3);

  pills = plain(content);
  lv_obj_set_size(pills, 290, 66);
  at(pills, CX, 342);
  denyPill = pill(pills, "Deny", COL_RED, &denyLbl);
  center(denyPill, -74, 0);
  allowPill = pill(pills, "Allow", COL_GREEN, &allowLbl);
  center(allowPill, 74, 0);

  hint = hintLabel(content, "Turn to choose");

  result = plain(root);
  lv_obj_set_size(result, 200, 200);
  at(result, CX, CY);
  lv_obj_set_style_radius(result, LV_RADIUS_CIRCLE, 0);
  lv_obj_set_style_bg_opa(result, LV_OPA_COVER, 0);
  resultIcon = label(result, &lv_font_montserrat_48, COL_BG, LV_SYMBOL_OK);
  center(resultIcon, 0, 0);
  hide(result, true);
}

void enter() { load(); }

void answer() {
  bool ok = choice == Choice::Allow;
  hostlink::sendAnswer(askId, ok);
  strncpy(answeredId, askId, sizeof answeredId - 1);
  hal::beep(ok ? Tone::Success : Tone::Select);
  answeredAt = lv_tick_get();
  if (answeredAt == 0) answeredAt = 1;
  lv_obj_set_style_bg_color(result, rgb(ok ? COL_GREEN : COL_RED), 0);
  setText(resultIcon, ok ? LV_SYMBOL_OK : LV_SYMBOL_CLOSE);
  fadeOut(content, 150);
  hide(result, false);
  pop(result, 360);
}

void choose(Choice c) {
  if (c == choice) return;
  choice = c;
  paintChoice();
  hal::beep(Tone::Tick);
}

void input(const InputEvent& e) {
  if (answeredAt) return;
  switch (e.type) {
    case Ev::Turn: {
      int n = turn.feed(e.value, 8.0f);
      if (n > 0) choose(Choice::Allow);
      else if (n < 0) choose(Choice::Deny);
      if (n) turn.reset();
      break;
    }
    case Ev::Swipe:
      if (e.dir == 'l') choose(Choice::Deny);
      else if (e.dir == 'r') choose(Choice::Allow);
      // a swipe up moves the text up, to what's below
      else if (e.dir == 'u') detail.scrollBy(58);
      else if (e.dir == 'd') detail.scrollBy(-58);
      break;
    case Ev::Tap:
      if (lv_tick_elaps(shownAt) < 600) break;  // a tap that was already on its way when this popped up
      if (choice == Choice::None) {
        shake(pills);
        hal::beep(Tone::Error);
        break;
      }
      answer();
      break;
    default:
      break;
  }
}

void update(uint32_t dirty) {
  if (!(dirty & D_ASK) || answeredAt) return;
  if (!g_model.askActive) {
    back(F_ALLOW);
  } else if (strcmp(askId, g_model.askId) != 0) {
    load();
    fadeIn(content, 200);
  }
}

void tick(uint32_t now) {
  if (!answeredAt || (int32_t)(now - answeredAt) < 750) return;
  answeredAt = 0;
  // Another request may be waiting right behind this one
  if (g_model.askActive && strcmp(askId, g_model.askId) != 0) {
    load();
    fadeIn(content, 200);
    return;
  }
  back(F_ALLOW);
}

void leave() {
  answeredAt = 0;
  lv_obj_set_style_opa(content, LV_OPA_COVER, 0);
}

}  // namespace

bool askAnswered() { return answeredId[0] && strcmp(answeredId, g_model.askId) == 0; }

const Face FACE_ALLOW = {"allow", "Claude Code", create, enter, leave, input, update, tick};

}  // namespace ui
