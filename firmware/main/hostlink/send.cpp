// The public send* functions: format a line and queue it. Safe from any task.
// Everything here only goes to paired hosts (F_TRUSTED); while there are none it returns early,
// before formatting anything, except sendLog, which the protocol allows before pairing.
#include <algorithm>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include "../hostlink.h"
#include "internal.h"
#include "json.h"

namespace hostlink {

using namespace detail;

namespace {

bool anyTrusted() { return txTrusted() != 0; }

template <typename Fill>
void sendTrusted(Fill fill) {
  if (!anyTrusted()) return;
  char line[384];
  size_t n = json::print(line, sizeof(line), fill);
  if (n) txLine(line, n, 0, F_TRUSTED);
}

void sendRaw(const char* line, int n) {
  if (n > 0) txLine(line, (size_t)n, 0, F_TRUSTED);
}

}  // namespace

void sendVolume(int v) {
  if (!anyTrusted()) return;
  txSetVolume(std::clamp(v, 0, 100));
}

void sendMute(bool m) {
  if (!anyTrusted()) return;
  char line[32];
  sendRaw(line, snprintf(line, sizeof(line), "{\"t\":\"mute\",\"m\":%s}", m ? "true" : "false"));
}

void sendMedia(const char* action) {
  sendTrusted([&](cJSON* d) {
    json::put(d, "t", "media");
    json::put(d, "a", action);
  });
}

void sendFace(const char* face) {
  sendTrusted([&](cJSON* d) {
    json::put(d, "t", "face");
    json::put(d, "f", face);
  });
}

void sendMove(char src, float x, float y) {
  if (!anyTrusted()) return;
  txAddMove(src, x, y);
}

void sendScroll(float y, float x) {
  if (!anyTrusted()) return;
  txAddScroll(y, x);
}

void sendClick(char button, int count) {
  if (!anyTrusted()) return;
  txFlushMouse();  // the pointer has to get there before the click does
  char line[48];
  sendRaw(line, snprintf(line, sizeof(line), "{\"t\":\"click\",\"b\":\"%c\",\"n\":%d}", button, count == 2 ? 2 : 1));
}

void sendButton(char button, bool down) {
  if (!anyTrusted()) return;
  txFlushMouse();
  char line[48];
  sendRaw(line, snprintf(line, sizeof(line), "{\"t\":\"btn\",\"b\":\"%c\",\"s\":\"%s\"}", button, down ? "down" : "up"));
}

void sendClaude(const char* action) {
  sendTrusted([&](cJSON* d) {
    json::put(d, "t", "claude");
    json::put(d, "a", action);
  });
}

void sendAnswer(const char* id, bool ok) {
  sendTrusted([&](cJSON* d) {
    json::put(d, "t", "ans");
    json::put(d, "id", id);
    json::put(d, "ok", ok);
  });
}

void sendDial(const char* action, int detents) {
  sendTrusted([&](cJSON* d) {
    json::put(d, "t", "dial");
    json::put(d, "a", action);
    if (!strcmp(action, "turn")) json::put(d, "d", detents);
  });
}

void sendGameTurn(float degrees) {
  if (!anyTrusted()) return;
  char line[64];
  sendRaw(line, snprintf(line, sizeof(line), "{\"t\":\"game\",\"in\":\"turn\",\"v\":%.2f}", (double)degrees));
}

void sendGame(const char* input, const char* value) {
  sendTrusted([&](cJSON* d) {
    json::put(d, "t", "game");
    json::put(d, "in", input);
    if (value) json::put(d, "v", value);
  });
}

void sendCall(const char* action) {
  sendTrusted([&](cJSON* d) {
    json::put(d, "t", "call");
    json::put(d, "a", action);
  });
}

void sendFocus(const char* state, int minutes) {
  sendTrusted([&](cJSON* d) {
    json::put(d, "t", "focus");
    json::put(d, "s", state);
    json::put(d, "min", minutes);
  });
}

void sendLog(const char* fmt, ...) {
  char msg[200];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(msg, sizeof(msg), fmt, ap);
  va_end(ap);
  char line[384];
  size_t n = json::print(line, sizeof(line), [&](cJSON* d) {
    json::put(d, "t", "log");
    json::put(d, "m", msg);
  });
  if (n) txLine(line, n, 0, 0);
}

}  // namespace hostlink
