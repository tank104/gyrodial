// The connection to the Windows app: BLE (NimBLE) and USB serial, both carrying NDJSON.
// See docs/PROTOCOL.md. Implemented in src/hostlink/.
//
// poll() runs in the main loop: it reads whatever arrived, applies it to g_model (marking dirty
// bits), and sends keepalives / battery. The send* functions are safe from any task (the input
// task calls sendMove/sendScroll/sendClick/sendButton directly in trackpad mode).
#pragma once
#include <stdint.h>
#include "model.h"

namespace hostlink {

void begin();
void poll();

bool linked();                      // a paired host is connected (same as g_model.linked)

// ---- system
void sendVolume(int v);             // 0..100; throttled to 30 Hz, the last value always goes out
void sendMute(bool m);
void sendMedia(const char* action); // "toggle" / "next" / "prev"
void sendFace(const char* face);    // the face now showing

// ---- mouse (coalesced: moves and scrolls are summed and flushed at most every 10 ms)
void sendMove(char src, float x, float y);   // src 't' trackpad / 'p' pointer
void sendScroll(float y, float x = 0);       // notches, + = up / right
void sendClick(char button, int count = 1);  // 'l' 'r' 'm'
void sendButton(char button, bool down);

// ---- integrations
void sendClaude(const char* action);         // "listen" / "send" / "cancel"
void sendAnswer(const char* id, bool ok);    // Claude Code permission answer
void sendDial(const char* action, int detents = 0);  // "turn" (detents) / "next" / "prev" / "tap"
void sendGameTurn(float degrees);
void sendGame(const char* input, const char* value = nullptr);  // "tap" "hold" "press" / "swipe" + "l"
void sendCall(const char* action);           // "mute" / "video" / "leave"
void sendFocus(const char* state, int minutes);
void sendLog(const char* fmt, ...);

// ---- pairing: the pairing face reports how it went
void pairResult(bool ok);
void pairProgress(int correct);    // numbers dialled in so far (0..3; 0 after a wrong one)

}  // namespace hostlink
