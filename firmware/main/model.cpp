#include "model.h"
#include "gyrodial_clock.h"
#include <atomic>

Model g_model;

static std::atomic<uint32_t> s_dirty{0};

void model_mark(uint32_t bits) { s_dirty.fetch_or(bits); }

uint32_t model_take_dirty() { return s_dirty.exchange(0); }

bool model_local_time(int* hour, int* minute, int* second) {
  if (!g_model.timeValid) return false;
  int64_t t = g_model.epochAtSync + (int64_t)(nowMs() - g_model.msAtSync) / 1000 + (int64_t)g_model.tzMin * 60;
  int64_t day = ((t % 86400) + 86400) % 86400;
  if (hour) *hour = (int)(day / 3600);
  if (minute) *minute = (int)(day / 60 % 60);
  if (second) *second = (int)(day % 60);
  return true;
}
