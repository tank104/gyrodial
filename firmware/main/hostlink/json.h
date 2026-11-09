// JSON for the protocol, on cJSON (ESP-IDF's `json` component): typed reads with defaults for the
// app's messages, and writers for ours. Every message is one flat object (docs/PROTOCOL.md).
#pragma once
#include <stddef.h>
#include <string.h>
#include <type_traits>
#include <cJSON.h>

namespace hostlink {
namespace detail {
namespace json {

// ---- reading. `o` is a parsed object; a missing key or a value of another type gives the default.
inline const cJSON* get(const cJSON* o, const char* key) { return cJSON_GetObjectItemCaseSensitive(o, key); }

inline bool isStr(const cJSON* o, const char* key) {
  const cJSON* v = get(o, key);
  return cJSON_IsString(v) && v->valuestring;
}
inline const char* str(const cJSON* o, const char* key, const char* def = nullptr) {
  const cJSON* v = get(o, key);
  return cJSON_IsString(v) && v->valuestring ? v->valuestring : def;
}

inline bool isNum(const cJSON* o, const char* key) { return cJSON_IsNumber(get(o, key)); }
inline double num(const cJSON* o, const char* key, double def = 0) {
  const cJSON* v = get(o, key);
  return cJSON_IsNumber(v) ? v->valuedouble : def;
}

inline bool isBool(const cJSON* o, const char* key) { return cJSON_IsBool(get(o, key)); }
inline bool boolean(const cJSON* o, const char* key, bool def = false) {
  const cJSON* v = get(o, key);
  return cJSON_IsBool(v) ? cJSON_IsTrue(v) : def;
}

// ---- writing
inline void put(cJSON* o, const char* key, const char* value) { cJSON_AddStringToObject(o, key, value ? value : ""); }
inline void put(cJSON* o, const char* key, bool value) { cJSON_AddBoolToObject(o, key, value); }
template <typename T, std::enable_if_t<std::is_arithmetic_v<T> && !std::is_same_v<T, bool>, int> = 0>
inline void put(cJSON* o, const char* key, T value) {
  cJSON_AddNumberToObject(o, key, (double)value);
}

// Builds one object with `fill(cJSON*)` and prints it into `out` (no allocation for the text).
// Returns its length, or 0 if it didn't fit or memory ran out.
template <typename Fill>
size_t print(char* out, size_t size, Fill fill) {
  cJSON* o = cJSON_CreateObject();
  if (!o) return 0;
  fill(o);
  // cJSON can overestimate what it needs by a few bytes; a line that close to the limit is dropped
  const bool ok = cJSON_PrintPreallocated(o, out, (int)size, false) != 0;
  cJSON_Delete(o);
  return ok ? strlen(out) : 0;
}

// Owns a parsed message for the length of a scope.
class Doc {
 public:
  Doc(const char* text, size_t len) : root_(cJSON_ParseWithLength(text, len)) {}
  ~Doc() { cJSON_Delete(root_); }
  Doc(const Doc&) = delete;
  Doc& operator=(const Doc&) = delete;
  // The message, or null if it wasn't valid JSON or not an object
  const cJSON* object() const { return cJSON_IsObject(root_) ? root_ : nullptr; }

 private:
  cJSON* root_;
};

}  // namespace json
}  // namespace detail
}  // namespace hostlink
