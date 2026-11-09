// Everything the dial knows about the computer it's paired with. Written only by hostlink (from the
// main loop), read by the faces. Fields that changed are reported through the dirty bits.
#pragma once
#include <stdint.h>
#include <stdbool.h>

enum class Transport : uint8_t { None, Usb, Ble };
enum class ClaudeState : uint8_t { Idle, Listening, Thinking, Reply, Error };
enum class DialKind : uint8_t { Num, Bool, Action, Select, Color, Text };
enum class NotifyKind : uint8_t { Info, Claude };

struct Model {
  // ---- link
  bool linked = false;          // a paired host is connected and talking
  Transport transport = Transport::None;
  char host[32] = "";           // the host's name (from hello)
  bool pairing = false;         // an unknown host is waiting: the pairing face should show
  char pairHost[32] = "";       // ...this host

  // ---- system audio, now playing
  int vol = 50;
  bool mute = false;
  bool playing = false;
  char title[96] = "";
  char artist[64] = "";
  char src[32] = "";

  // ---- time
  bool timeValid = false;
  int64_t epochAtSync = 0;      // unix seconds (UTC) at msAtSync
  uint32_t msAtSync = 0;
  int tzMin = 0;                // minutes east of UTC

  // ---- Claude
  ClaudeState claude = ClaudeState::Idle;
  char claudeText[640] = "";
  float claudeLvl = 0;

  // ---- Claude Code permission request
  bool askActive = false;
  char askId[32] = "";
  char askTool[40] = "";
  char askText[256] = "";
  char askProj[48] = "";

  // ---- banner
  char notifyText[128] = "";
  NotifyKind notifyKind = NotifyKind::Info;
  uint32_t notifyAt = 0;        // nowMs() it arrived

  // ---- DialKit
  bool dialOn = false;
  char dialPanel[40] = "";
  char dialName[48] = "";
  DialKind dialKind = DialKind::Num;
  float dialVal = 0, dialMin = 0, dialMax = 1, dialStep = 0;
  bool dialBool = false;
  char dialTxt[48] = "";
  int dialIdx = 0, dialCnt = 0;

  // ---- dialkit-watt game page
  bool gameOn = false;
  char gameLabel[32] = "";
  float gameAngle = 0;

  // ---- Teams meeting
  bool callOn = false;
  bool callMuted = false;
  bool callVideo = false;
  char callApp[16] = "";

  // ---- device settings from the app
  int bright = 80;              // 10..100
  bool sound = true;
  int dimSec = 30;              // 0 = never
  int offSec = 300;             // 0 = never
  float sens = 1.0f;            // turn sensitivity
  bool flip = false;            // reverse turn direction

  // ---- power (copied from hal by hostlink)
  int bat = -1;                 // percent, -1 = no battery
  bool chg = false;
  bool usb = false;

  // ---- a face the host asked for (face id), consumed by the faces
  char faceRequest[16] = "";
};

extern Model g_model;

enum Dirty : uint32_t {
  D_LINK = 1u << 0,
  D_AUDIO = 1u << 1,   // vol, mute
  D_MEDIA = 1u << 2,   // playing, title, artist, src
  D_TIME = 1u << 3,
  D_CLAUDE = 1u << 4,
  D_ASK = 1u << 5,
  D_NOTIFY = 1u << 6,
  D_DIAL = 1u << 7,
  D_GAME = 1u << 8,
  D_CALL = 1u << 9,
  D_CFG = 1u << 10,
  D_BAT = 1u << 11,
  D_PAIR = 1u << 12,
  D_FACE_REQ = 1u << 13,
};

void model_mark(uint32_t bits);   // safe from any task
uint32_t model_take_dirty();      // returns and clears the dirty bits

// Local time now, from the last sync. Returns false if the host never sent the time.
bool model_local_time(int* hour, int* minute, int* second);
