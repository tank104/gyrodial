// The protocol (docs/PROTOCOL.md): per-transport host sessions, the handshake and pairing,
// keepalive, battery reports, and turning the app's messages into g_model.
// Everything here runs in the main loop, from poll() or from the faces (pairResult).
#include <stdio.h>
#include <algorithm>
#include <esp_app_desc.h>
#include <esp_mac.h>
#include <math.h>
#include <string.h>
#include "puck_clock.h"
#include "../config.h"
#include "../hal.h"
#include "../hostlink.h"
#include "../model.h"
#include "../ui.h"
#include "internal.h"
#include "json.h"

namespace hostlink {

using namespace detail;

namespace {

// One per transport. USB and BLE are separate sessions, even when the same PC is on both: each
// says hello on its own, and each is trusted only once its host id is known.
struct Session {
  bool alive = false;   // USB: heard from within LINK_HOST_TIMEOUT_MS; BLE: connected
  bool hello = false;   // the host introduced itself
  bool paired = false;  // ...and its id is in the store
  char id[17] = "";
  char name[32] = "";
  uint32_t lastRx = 0;
};

constexpr int kUsb = 0, kBle = 1;
constexpr uint32_t kRejectMs = 60000;      // a host that failed pairing is ignored this long
constexpr uint32_t kRetryAfterFailMs = 5000;  // ...unless the app explicitly asks again after this
constexpr uint32_t kBatEveryMs = 60000;
constexpr uint32_t kBootHelloMs = 600;
constexpr uint32_t kPairTimeoutMs = 180000;  // the pairing face gives up (and lets the screen rest)

Session s_sess[2];
char s_name[12] = "Puck";
char s_puckId[17] = "";

int s_pairIdx = -1;  // the session that's pairing, or -1
char s_pairId[17] = "";
uint32_t s_pairStartedAt = 0;
struct Reject {
  char id[17];
  uint32_t at;
};
Reject s_reject[4] = {};
uint32_t s_lastFailAt = 0;

bool s_bootHelloSent = false;
uint32_t s_bleDropAt = 0;  // when to drop a BLE host that failed pairing (after "pair fail" is out)
int s_batSentP = -2;
bool s_batSentChg = false, s_batSentUsb = false;
uint32_t s_batSentAt = 0;

uint8_t tpOf(int idx) { return idx == kUsb ? TP_USB : TP_BLE; }
int idxOf(uint8_t tp) { return tp == TP_USB ? kUsb : kBle; }

// Transports whose session belongs to this host id.
uint8_t tpsOfHost(const char* id) {
  uint8_t m = 0;
  for (int i = 0; i < 2; i++)
    if (s_sess[i].alive && s_sess[i].hello && strcmp(s_sess[i].id, id) == 0) m |= tpOf(i);
  return m;
}

// Milliseconds this host is still refused for (0 = not refused)
uint32_t rejectedFor(const char* id) {
  uint32_t now = nowMs();
  for (auto& r : s_reject) {
    if (!r.id[0] || strcmp(r.id, id) != 0) continue;
    int32_t left = (int32_t)kRejectMs - (int32_t)(now - r.at);
    if (left > 0) return (uint32_t)left;
  }
  return 0;
}

bool rejected(const char* id) { return rejectedFor(id) > 0; }

void reject(const char* id) {
  int slot = 0;
  for (int i = 0; i < 4; i++) {
    if (strcmp(s_reject[i].id, id) == 0) {
      slot = i;
      break;
    }
    if (s_reject[i].at < s_reject[slot].at) slot = i;
  }
  copyText(s_reject[slot].id, sizeof(s_reject[slot].id), id);
  s_reject[slot].at = nowMs();
}

void unreject(const char* id) {
  for (auto& r : s_reject)
    if (strcmp(r.id, id) == 0) r.id[0] = 0;
}

template <typename Fill>
void sendJson(uint8_t targets, uint8_t flags, Fill fill) {
  char line[768];  // the longest is a diag reply
  size_t n = json::print(line, sizeof(line), fill);
  if (n) txLine(line, n, targets, flags);
}

// unprompted: nobody asked (boot, BLE subscribe). Marked "new" so the app answers with its own
// hello instead of reading "paired" from a session that doesn't know it yet.
void sendHello(uint8_t targets, uint8_t flags = 0, bool unprompted = false) {
  // "paired" is per transport, so a broadcast hello would lie to one of them: send one each.
  for (int i = 0; i < 2; i++) {
    uint8_t tp = tpOf(i);
    if (!(targets & tp)) continue;
    bool paired = s_sess[i].paired;
    sendJson(tp, flags, [&](cJSON* d) {
      json::put(d, "t", "hello");
      json::put(d, "v", PUCK_PROTOCOL_VERSION);
      json::put(d, "fw", esp_app_get_description()->version);
      json::put(d, "id", s_puckId);
      json::put(d, "name", s_name);
      json::put(d, "paired", paired);
      if (unprompted) json::put(d, "new", true);
      json::put(d, "bat", hal::batteryPercent());
      json::put(d, "chg", hal::charging());
      json::put(d, "usb", hal::usbPowered());
      json::put(d, "face", ui::currentFace());
    });
  }
}

void sendPair(uint8_t targets, const char* state) {
  if (!targets) return;
  sendJson(targets, 0, [&](cJSON* d) {
    json::put(d, "t", "pair");
    json::put(d, "s", state);
  });
}

void sendPong(uint8_t tp) {
  static const char line[] = "{\"t\":\"pong\"}";
  txLine(line, sizeof(line) - 1, tp, 0);
}

// Host state the faces must not keep showing once nobody is connected.
void clearTransient() {
  if (g_model.askActive) {
    g_model.askActive = false;
    model_mark(D_ASK);
  }
  if (g_model.dialOn) {
    g_model.dialOn = false;
    model_mark(D_DIAL);
  }
  if (g_model.gameOn) {
    g_model.gameOn = false;
    model_mark(D_GAME);
  }
  if (g_model.callOn) {
    g_model.callOn = false;
    model_mark(D_CALL);
  }
  if (g_model.claude != ClaudeState::Idle) {
    g_model.claude = ClaudeState::Idle;
    g_model.claudeText[0] = 0;
    g_model.claudeLvl = 0;
    model_mark(D_CLAUDE);
  }
}

void updateLink() {
  const Session& u = s_sess[kUsb];
  const Session& b = s_sess[kBle];
  bool usbLinked = u.alive && u.paired;
  bool bleLinked = b.alive && b.paired;
  txSetAlive((u.alive ? TP_USB : 0) | (b.alive ? TP_BLE : 0));
  txSetTrusted((usbLinked ? TP_USB : 0) | (bleLinked ? TP_BLE : 0));

  bool linked = usbLinked || bleLinked;
  Transport t = usbLinked ? Transport::Usb : bleLinked ? Transport::Ble : Transport::None;
  const char* host = usbLinked ? u.name : bleLinked ? b.name : "";
  if (linked != g_model.linked || t != g_model.transport || strcmp(host, g_model.host) != 0) {
    bool was = g_model.linked;
    g_model.linked = linked;
    g_model.transport = t;
    copyText(g_model.host, sizeof(g_model.host), host);
    model_mark(D_LINK);
    if (was && !linked) clearTransient();
  }
}

void endPairing() {
  s_pairIdx = -1;
  s_pairId[0] = 0;
  if (g_model.pairing) {
    g_model.pairing = false;
    model_mark(D_PAIR);
  }
}

void startPairing(int idx) {
  Session& s = s_sess[idx];
  if (s_pairIdx >= 0 && strcmp(s_pairId, s.id) != 0) return;  // someone else is mid-pairing: wait
  if (s_pairIdx < 0) s_pairStartedAt = nowMs();  // a repeat "start" from the same PC keeps the clock
  s_pairIdx = idx;
  copyText(s_pairId, sizeof(s_pairId), s.id);
  copyText(g_model.pairHost, sizeof(g_model.pairHost), s.name);
  g_model.pairing = true;
  model_mark(D_PAIR);
  sendPair(tpsOfHost(s.id), "start");
}

void sendRefused(int idx, const char* id) {
  uint32_t left = rejectedFor(id);
  sendJson(tpOf(idx), 0, [&](cJSON* d) {
    json::put(d, "t", "pair");
    json::put(d, "s", "refused");
    json::put(d, "retry", (left + 999) / 1000);
  });
}

// The app asking to pair is a person clicking "Pair" (or plugging the puck in), so it may override a
// recent cancel on the puck, but not instantly: an app that retried in a loop would make cancelling
// on the puck impossible.
bool mayPair(int idx) {
  Session& s = s_sess[idx];
  if (!rejected(s.id)) return true;
  if ((int32_t)(nowMs() - s_lastFailAt) < (int32_t)kRetryAfterFailMs) {
    sendRefused(idx, s.id);
    return false;
  }
  unreject(s.id);
  return true;
}

// The host on this transport is gone. keepIdentity (USB gone quiet): the cable is still the
// same PC's, so if it starts talking again it's trusted without a new hello. A BLE disconnect
// forgets everything: the next central may be another PC.
void endSession(int idx, bool keepIdentity = false) {
  Session& s = s_sess[idx];
  char id[17];
  copyText(id, sizeof(id), s.id);
  if (keepIdentity) {
    s.alive = false;
  } else {
    s = Session();
  }
  if (s_pairIdx == idx) {
    // The same PC may still be on the other transport: let the pairing carry on there.
    int other = 1 - idx;
    if (s_sess[other].alive && s_sess[other].hello && strcmp(s_sess[other].id, id) == 0)
      s_pairIdx = other;
    else
      endPairing();
  }
  updateLink();
}

// ---- incoming

void copyIf(const cJSON* d, const char* key, char* dst, size_t size, bool& changed) {
  const char* s = json::str(d, key);
  if (!s) return;
  // Compare what would be stored (a long title is stored truncated), so resending the same
  // state doesn't count as a change.
  char cut[128];
  copyText(cut, size < sizeof(cut) ? size : sizeof(cut), s);
  if (strcmp(cut, dst) == 0) return;
  copyText(dst, size, s);
  changed = true;
}

void numIf(const cJSON* d, const char* key, float& dst, bool& changed) {
  if (!json::isNum(d, key)) return;
  float f = (float)json::num(d, key);
  if (!isfinite(f) || f == dst) return;
  dst = f;
  changed = true;
}

void boolIf(const cJSON* d, const char* key, bool& dst, bool& changed) {
  if (!json::isBool(d, key)) return;
  bool b = json::boolean(d, key);
  if (b == dst) return;
  dst = b;
  changed = true;
}

void intIf(const cJSON* d, const char* key, int& dst, int lo, int hi, bool& changed) {
  if (!json::isNum(d, key)) return;
  double f = json::num(d, key);
  if (!isfinite(f)) return;
  int i = (int)lround(std::clamp(f, (double)lo, (double)hi));
  if (i == dst) return;
  dst = i;
  changed = true;
}

void applyState(const cJSON* d) {
  bool audio = false, media = false;
  intIf(d, "vol", g_model.vol, 0, 100, audio);
  boolIf(d, "mute", g_model.mute, audio);
  boolIf(d, "play", g_model.playing, media);
  copyIf(d, "title", g_model.title, sizeof(g_model.title), media);
  copyIf(d, "artist", g_model.artist, sizeof(g_model.artist), media);
  copyIf(d, "src", g_model.src, sizeof(g_model.src), media);
  if (audio) model_mark(D_AUDIO);
  if (media) model_mark(D_MEDIA);
}

void applyTime(const cJSON* d) {
  if (!json::isNum(d, "epoch")) return;
  g_model.epochAtSync = (int64_t)json::num(d, "epoch");
  g_model.msAtSync = nowMs();
  if (json::isNum(d, "tz") && isfinite(json::num(d, "tz")))
    g_model.tzMin = (int)lround(std::clamp(json::num(d, "tz"), -14.0 * 60, 14.0 * 60));
  g_model.timeValid = true;
  model_mark(D_TIME);
}

void applyCfg(const cJSON* d) {
  bool changed = false;
  intIf(d, "bright", g_model.bright, 10, 100, changed);
  intIf(d, "dim", g_model.dimSec, 0, 65535, changed);
  intIf(d, "off", g_model.offSec, 0, 65535, changed);
  if (json::isBool(d, "sound")) {
    boolIf(d, "sound", g_model.sound, changed);
    hal::setSoundEnabled(g_model.sound);
  }
  if (json::isNum(d, "sens")) {
    float s = (float)json::num(d, "sens");
    if (isfinite(s)) {
      s = std::clamp(s, 0.25f, 4.0f);
      if (s != g_model.sens) {
        g_model.sens = s;
        changed = true;
      }
      hal::setTurnSensitivity(g_model.sens);
    }
  }
  if (json::isBool(d, "flip")) {
    boolIf(d, "flip", g_model.flip, changed);
    hal::setTurnFlip(g_model.flip);
  }
  if (changed) {
    model_mark(D_CFG);
    storeSaveCfgSoon();
  }
}

void applyClaude(const cJSON* d) {
  const char* s = json::str(d, "s");
  if (s) {
    ClaudeState st = g_model.claude;
    if (!strcmp(s, "idle")) st = ClaudeState::Idle;
    else if (!strcmp(s, "listening")) st = ClaudeState::Listening;
    else if (!strcmp(s, "thinking")) st = ClaudeState::Thinking;
    else if (!strcmp(s, "reply")) st = ClaudeState::Reply;
    else if (!strcmp(s, "error")) st = ClaudeState::Error;
    // A new state without text starts with an empty one (e.g. "thinking" after a transcript).
    if (st != g_model.claude && !json::isStr(d, "text")) g_model.claudeText[0] = 0;
    g_model.claude = st;
  }
  if (json::isStr(d, "text"))
    copyText(g_model.claudeText, sizeof(g_model.claudeText), json::str(d, "text", ""));
  if (json::isNum(d, "lvl")) {
    float l = (float)json::num(d, "lvl");
    g_model.claudeLvl = isfinite(l) ? std::clamp(l, 0.0f, 1.0f) : 0;
  }
  model_mark(D_CLAUDE);
}

void applyAsk(const cJSON* d) {
  // A new ask replaces the one showing: the app queues them and sends the next one when it's ready.
  copyText(g_model.askId, sizeof(g_model.askId), json::str(d, "id", ""));
  copyText(g_model.askTool, sizeof(g_model.askTool), json::str(d, "tool", ""));
  copyText(g_model.askText, sizeof(g_model.askText), json::str(d, "text", ""));
  copyText(g_model.askProj, sizeof(g_model.askProj), json::str(d, "proj", ""));
  g_model.askActive = true;
  model_mark(D_ASK);
}

void applyAskEnd(const cJSON* d) {
  const char* id = json::str(d, "id", "");
  if (g_model.askActive && strcmp(id, g_model.askId) == 0) {
    g_model.askActive = false;
    model_mark(D_ASK);
  }
}

void applyNotify(const cJSON* d) {
  copyText(g_model.notifyText, sizeof(g_model.notifyText), json::str(d, "text", ""));
  const char* kind = json::str(d, "kind", "info");
  g_model.notifyKind = strcmp(kind, "claude") == 0 ? NotifyKind::Claude : NotifyKind::Info;
  g_model.notifyAt = nowMs();
  model_mark(D_NOTIFY);
}

DialKind dialKindOf(const char* k, DialKind fallback) {
  if (!k) return fallback;
  if (!strcmp(k, "num")) return DialKind::Num;
  if (!strcmp(k, "bool")) return DialKind::Bool;
  if (!strcmp(k, "action")) return DialKind::Action;
  if (!strcmp(k, "select")) return DialKind::Select;
  if (!strcmp(k, "color")) return DialKind::Color;
  if (!strcmp(k, "text")) return DialKind::Text;
  return fallback;
}

void applyDial(const cJSON* d) {
  bool c = true;  // a dial message always redraws: values and labels change together
  boolIf(d, "on", g_model.dialOn, c);
  copyIf(d, "panel", g_model.dialPanel, sizeof(g_model.dialPanel), c);
  copyIf(d, "name", g_model.dialName, sizeof(g_model.dialName), c);
  g_model.dialKind = dialKindOf(json::str(d, "kind"), g_model.dialKind);
  numIf(d, "val", g_model.dialVal, c);
  numIf(d, "min", g_model.dialMin, c);
  numIf(d, "max", g_model.dialMax, c);
  numIf(d, "step", g_model.dialStep, c);
  boolIf(d, "on2", g_model.dialBool, c);
  copyIf(d, "txt", g_model.dialTxt, sizeof(g_model.dialTxt), c);
  intIf(d, "idx", g_model.dialIdx, 0, 9999, c);
  intIf(d, "cnt", g_model.dialCnt, 0, 9999, c);
  model_mark(D_DIAL);
}

void applyGame(const cJSON* d) {
  bool c = false;
  boolIf(d, "on", g_model.gameOn, c);
  copyIf(d, "label", g_model.gameLabel, sizeof(g_model.gameLabel), c);
  numIf(d, "angle", g_model.gameAngle, c);
  if (c) model_mark(D_GAME);
}

void applyCall(const cJSON* d) {
  bool c = false;
  boolIf(d, "on", g_model.callOn, c);
  boolIf(d, "muted", g_model.callMuted, c);
  boolIf(d, "video", g_model.callVideo, c);
  copyIf(d, "app", g_model.callApp, sizeof(g_model.callApp), c);
  if (c) model_mark(D_CALL);
}

void applyFace(const cJSON* d) {
  const char* f = json::str(d, "f", "");
  if (!*f) return;
  copyText(g_model.faceRequest, sizeof(g_model.faceRequest), f);
  model_mark(D_FACE_REQ);
}

void onHello(int idx, const cJSON* d) {
  Session& s = s_sess[idx];
  char id[17], name[32];
  copyText(id, sizeof(id), json::str(d, "id", ""));
  copyText(name, sizeof(name), json::str(d, "host", "PC"));
  if (!*id) return;  // not one of ours

  if (s.hello && strcmp(s.id, id) != 0 && s_pairIdx == idx) endPairing();  // another PC on this port
  s.hello = true;
  copyText(s.id, sizeof(s.id), id);
  copyText(s.name, sizeof(s.name), name);
  s.paired = storeHasHost(id);
  if (s.paired) storeAddHost(id, name);  // keep the name fresh (a renamed PC)

  sendHello(tpOf(idx));
  if (!s.paired) {
    // A bare hello (an app reconnecting on its own) doesn't override a cancel on the puck: say so,
    // so the app can tell the person when it will work again
    if (rejected(id)) sendRefused(idx, id);
    else startPairing(idx);
  }
  updateLink();
}

void onPairStart(int idx) {
  Session& s = s_sess[idx];
  if (!s.hello) return;  // the app re-sends hello before "pair start", so this is a puck that just rebooted
  if (s.paired) {
    sendPair(tpOf(idx), "ok");
    return;
  }
  if (mayPair(idx)) startPairing(idx);
}

void onUnpair(int idx) {
  Session& s = s_sess[idx];
  if (!s.hello) return;
  storeRemoveHost(s.id);
  for (auto& other : s_sess)
    if (other.hello && strcmp(other.id, s.id) == 0) other.paired = false;
  sendPair(tpsOfHost(s.id), "none");
  updateLink();
}

void pollBattery(uint32_t now) {
  int p = hal::batteryPercent();
  bool chg = hal::charging();
  bool usb = hal::usbPowered();
  if (p != g_model.bat || chg != g_model.chg || usb != g_model.usb) {
    g_model.bat = p;
    g_model.chg = chg;
    g_model.usb = usb;
    model_mark(D_BAT);
  }
  // 2 points, not 1: the estimate wobbles by a point while charging
  bool due = s_batSentP == -2 || abs(p - s_batSentP) >= 2 || chg != s_batSentChg || usb != s_batSentUsb ||
             (int32_t)(now - s_batSentAt) >= (int32_t)kBatEveryMs;
  if (!due) return;
  s_batSentP = p;
  s_batSentChg = chg;
  s_batSentUsb = usb;
  s_batSentAt = now;
  int mv = hal::batteryMv();
  sendJson(0, 0, [&](cJSON* d) {
    json::put(d, "t", "bat");
    json::put(d, "p", p);
    json::put(d, "chg", chg);
    json::put(d, "usb", usb);
    json::put(d, "mv", mv);
  });
}

}  // namespace

// ---- called by the transports (main loop)

namespace detail {

void onLine(uint8_t tp, char* line, size_t len) {
  if (!len || line[0] != '{') return;  // boot logs and other noise
  json::Doc doc(line, len);
  const cJSON* d = doc.object();
  if (!d) return;
  const char* t = json::str(d, "t");
  if (!t) return;

  int idx = idxOf(tp);
  Session& s = s_sess[idx];
  s.lastRx = nowMs();
  if (!s.alive) {
    s.alive = true;
    // USB came back after a quiet spell with the same PC: if it was waiting to pair, carry on
    if (s.hello && !s.paired && s_pairIdx < 0 && !rejected(s.id)) startPairing(idx);
    updateLink();
  }

  if (!strcmp(t, "hello")) return onHello(idx, d);
  if (!strcmp(t, "ping")) return sendPong(tp);
  if (!strcmp(t, "diag")) {
    // Read-only board state for debugging, answered even before pairing (it reveals nothing
    // about the paired PC)
    char state[560];
    hal::diag(state, sizeof(state));
    char line[sizeof(state) + 96];
    snprintf(line, sizeof(line), "%s face=%s pairing=%d linked=%d", state, ui::currentFace(), (int)g_model.pairing,
             (int)g_model.linked);
    sendJson(tp, 0, [&](cJSON* o) {
      json::put(o, "t", "diag");
      json::put(o, "m", line);
    });
    return;
  }
  if (!strcmp(t, "pair")) {
    const char* a = json::str(d, "a", "");
    if (!strcmp(a, "start")) onPairStart(idx);
    // Cancelled on the PC: stop showing the numbers, but don't refuse that PC (it was its choice)
    else if (!strcmp(a, "cancel") && s_pairIdx >= 0 && s.hello && strcmp(s.id, s_pairId) == 0) endPairing();
    return;
  }
  if (!s.paired) return;  // not trusted yet: nothing else counts

  if (!strcmp(t, "state")) applyState(d);
  else if (!strcmp(t, "time")) applyTime(d);
  else if (!strcmp(t, "cfg")) applyCfg(d);
  else if (!strcmp(t, "claude")) applyClaude(d);
  else if (!strcmp(t, "ask")) applyAsk(d);
  else if (!strcmp(t, "ask_end")) applyAskEnd(d);
  else if (!strcmp(t, "notify")) applyNotify(d);
  else if (!strcmp(t, "dial")) applyDial(d);
  else if (!strcmp(t, "game")) applyGame(d);
  else if (!strcmp(t, "call")) applyCall(d);
  else if (!strcmp(t, "face")) applyFace(d);
  else if (!strcmp(t, "unpair")) onUnpair(idx);
}

void onBleConnected() {
  // A central connected; it's a session once it subscribes and says hello.
  s_sess[kBle] = Session();
  s_sess[kBle].alive = true;
  s_sess[kBle].lastRx = nowMs();
  updateLink();
}

void onBleSubscribed() {
  // Our side of "hello is also sent unprompted on every transport".
  sendHello(TP_BLE, 0, true);
}

void onBleDisconnected() { endSession(kBle); }

}  // namespace detail

// ---- public

void begin() {
  uint8_t mac[6] = {};
  esp_read_mac(mac, ESP_MAC_BT);
  snprintf(s_name, sizeof(s_name), "Puck-%02X%02X", mac[4], mac[5]);
  snprintf(s_puckId, sizeof(s_puckId), "%016llx", (unsigned long long)hal::chipId());

  storeBegin();
  storeLoadCfg();
  txBegin();
  usbBegin();
  bleBegin(s_name);
  updateLink();
}

void poll() {
  usbPoll();
  blePoll();
  // After the transports: a line handled just now stamps lastRx with a later nowMs() than one
  // read before them, and every comparison below is signed anyway.
  uint32_t now = nowMs();

  Session& u = s_sess[kUsb];
  if (u.alive && (int32_t)(now - u.lastRx) > (int32_t)LINK_HOST_TIMEOUT_MS) endSession(kUsb, true);

  if (!s_bootHelloSent && now >= kBootHelloMs) {
    // Lets an app that already has the port open notice a freshly booted puck right away.
    s_bootHelloSent = true;
    sendHello(TP_USB, F_FORCE, true);
  }

  if (s_bleDropAt && (int32_t)(now - s_bleDropAt) >= 300) {
    s_bleDropAt = 0;
    bleDisconnect();
  }

  // Nobody finished pairing: stop holding the screen on (the host isn't refused; it may ask again)
  if (s_pairIdx >= 0 && (int32_t)(now - s_pairStartedAt) > (int32_t)kPairTimeoutMs) {
    uint8_t tps = tpsOfHost(s_pairId);
    endPairing();
    sendPair(tps, "timeout");
  }

  pollBattery(now);
  storePoll();
}

bool linked() { return g_model.linked; }

void pairProgress(int correct) {
  if (s_pairIdx < 0) return;
  uint8_t tps = tpsOfHost(s_pairId);
  if (!tps) return;
  sendJson(tps, 0, [&](cJSON* d) {
    json::put(d, "t", "pair");
    json::put(d, "s", "progress");
    json::put(d, "n", correct);
  });
}

void pairResult(bool ok) {
  if (s_pairIdx < 0) return;
  char id[17];
  copyText(id, sizeof(id), s_pairId);
  uint8_t tps = tpsOfHost(id);
  if (ok) {
    storeAddHost(id, s_sess[s_pairIdx].name);
    // Tell every transport this PC said hello on, even one that has gone quiet for a moment:
    // a PC that never hears "ok" would sit in "pairing" while the puck already trusts it
    uint8_t told = 0;
    for (int i = 0; i < 2; i++) {
      Session& s = s_sess[i];
      if (s.hello && strcmp(s.id, id) == 0) {
        s.paired = true;
        told |= tpOf(i);
      }
    }
    endPairing();
    updateLink();
    sendJson(told, F_FORCE, [&](cJSON* d) {
      json::put(d, "t", "pair");
      json::put(d, "s", "ok");
    });
    sendHello(told, F_FORCE);
  } else {
    sendPair(tps, "fail");
    reject(id);
    s_lastFailAt = nowMs();
    endPairing();
    // Over BLE the unwanted PC would otherwise hold the only connection. Give "pair fail" a
    // moment to go out first.
    if (s_sess[kBle].hello && strcmp(s_sess[kBle].id, id) == 0) s_bleDropAt = nowMs() | 1;
    updateLink();
  }
}

}  // namespace hostlink
