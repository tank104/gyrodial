// What survives a reboot: the hosts this puck is paired with, and the last cfg the app sent
// (so brightness, sound and turn direction are right before any PC connects).
// Everything here runs in the main loop.
#include <string.h>
#include <algorithm>
#include <nvs.h>
#include "puck_clock.h"
#include "../config.h"
#include "../hal.h"
#include "../model.h"
#include "internal.h"

namespace hostlink {
namespace detail {

namespace {

struct HostRecord {
  char id[17];
  char name[32];
  uint32_t seq;  // when it was paired; the smallest is evicted first
};

struct HostTable {
  uint8_t version;
  uint8_t count;
  uint32_t nextSeq;
  HostRecord hosts[PAIR_MAX_HOSTS];
};

struct CfgBlob {
  uint8_t version;
  uint8_t bright;
  uint8_t sound;
  uint8_t flip;
  uint16_t dimSec;
  uint16_t offSec;
  float sens;
};

constexpr uint8_t kHostsVersion = 1;
constexpr uint8_t kCfgVersion = 1;
constexpr uint32_t kCfgSaveDelayMs = 2000;  // a slider dragged in Settings sends many cfgs

nvs_handle_t s_nvs = 0;
bool s_open = false;
HostTable s_table = {};
uint32_t s_cfgDirtyAt = 0;

// The blobs keep the keys and layouts the firmware has always used, so pairings survive updates.
void putBlob(const char* key, const void* data, size_t len) {
  if (!s_open) return;
  if (nvs_set_blob(s_nvs, key, data, len) == ESP_OK) nvs_commit(s_nvs);
}

// True only if the key holds exactly `len` bytes, now copied to `out`
bool getBlob(const char* key, void* out, size_t len) {
  if (!s_open) return false;
  size_t have = 0;
  if (nvs_get_blob(s_nvs, key, nullptr, &have) != ESP_OK || have != len) return false;
  return nvs_get_blob(s_nvs, key, out, &have) == ESP_OK && have == len;
}

void saveHosts() { putBlob("hosts", &s_table, sizeof(s_table)); }

int find(const char* id) {
  for (int i = 0; i < s_table.count; i++)
    if (strcmp(s_table.hosts[i].id, id) == 0) return i;
  return -1;
}

void saveCfg() {
  CfgBlob b = {};
  b.version = kCfgVersion;
  b.bright = (uint8_t)g_model.bright;
  b.sound = g_model.sound;
  b.flip = g_model.flip;
  b.dimSec = (uint16_t)g_model.dimSec;
  b.offSec = (uint16_t)g_model.offSec;
  b.sens = g_model.sens;
  putBlob("cfg", &b, sizeof(b));
}

}  // namespace

void copyText(char* dst, size_t size, const char* src) {
  if (!size) return;
  if (!src) src = "";
  size_t n = strlen(src);
  if (n >= size) {
    n = size - 1;
    // Back off to the start of a UTF-8 character, so the faces never draw half of one.
    while (n > 0 && ((uint8_t)src[n] & 0xC0) == 0x80) n--;
  }
  memcpy(dst, src, n);
  dst[n] = 0;
}

void storeBegin() {
  s_open = nvs_open("puck", NVS_READWRITE, &s_nvs) == ESP_OK;
  HostTable t = {};
  if (getBlob("hosts", &t, sizeof(t)) && t.version == kHostsVersion && t.count <= PAIR_MAX_HOSTS) {
    s_table = t;
    for (int i = 0; i < s_table.count; i++) {
      s_table.hosts[i].id[sizeof(s_table.hosts[i].id) - 1] = 0;
      s_table.hosts[i].name[sizeof(s_table.hosts[i].name) - 1] = 0;
    }
  } else {
    s_table = {};
    s_table.version = kHostsVersion;
  }
}

bool storeHasHost(const char* id) { return id && *id && find(id) >= 0; }

void storeAddHost(const char* id, const char* name) {
  if (!id || !*id) return;
  int i = find(id);
  if (i < 0) {
    if (s_table.count < PAIR_MAX_HOSTS) {
      i = s_table.count++;
    } else {
      i = 0;
      for (int k = 1; k < s_table.count; k++)
        if (s_table.hosts[k].seq < s_table.hosts[i].seq) i = k;
    }
    copyText(s_table.hosts[i].id, sizeof(s_table.hosts[i].id), id);
    s_table.hosts[i].seq = s_table.nextSeq++;
  } else {
    char fresh[sizeof(s_table.hosts[i].name)];
    copyText(fresh, sizeof(fresh), name);
    if (strcmp(fresh, s_table.hosts[i].name) == 0) return;  // nothing new: spare the flash
  }
  copyText(s_table.hosts[i].name, sizeof(s_table.hosts[i].name), name);
  saveHosts();
}

bool storeRemoveHost(const char* id) {
  int i = find(id);
  if (i < 0) return false;
  for (int k = i; k + 1 < s_table.count; k++) s_table.hosts[k] = s_table.hosts[k + 1];
  s_table.count--;
  memset(&s_table.hosts[s_table.count], 0, sizeof(HostRecord));
  saveHosts();
  return true;
}

void storeLoadCfg() {
  CfgBlob b = {};
  if (getBlob("cfg", &b, sizeof(b)) && b.version == kCfgVersion) {
    g_model.bright = std::clamp((int)b.bright, 10, 100);
    g_model.sound = b.sound != 0;
    g_model.flip = b.flip != 0;
    g_model.dimSec = b.dimSec;
    g_model.offSec = b.offSec;
    g_model.sens = (b.sens >= 0.25f && b.sens <= 4.0f) ? b.sens : 1.0f;
  }
  hal::setTurnSensitivity(g_model.sens);
  hal::setTurnFlip(g_model.flip);
  hal::setSoundEnabled(g_model.sound);
  model_mark(D_CFG);
}

void storeSaveCfgSoon() { s_cfgDirtyAt = nowMs() | 1; }

void storePoll() {
  if (s_cfgDirtyAt && (int32_t)(nowMs() - s_cfgDirtyAt) >= (int32_t)kCfgSaveDelayMs) {
    s_cfgDirtyAt = 0;
    saveCfg();
  }
}

}  // namespace detail
}  // namespace hostlink
