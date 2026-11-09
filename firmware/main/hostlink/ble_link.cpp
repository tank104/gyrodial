// Bluetooth LE on ESP-IDF's NimBLE host: one service with an RX characteristic the app writes into
// and a TX characteristic the puck notifies on. Together they carry the same NDJSON byte stream as
// USB, cut into MTU-sized pieces (a piece can hold part of a line or several lines).
//
// NimBLE's callbacks run on the NimBLE host task. They only hand bytes and events over to poll()
// through FreeRTOS buffers; the protocol itself always runs in the main loop.
#include <string.h>
#include <atomic>
#include <esp_bt.h>
#include <esp_log.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/stream_buffer.h>
#include <host/ble_hs.h>
#include <host/util/util.h>
#include <nimble/nimble_port.h>
#include <nimble/nimble_port_freertos.h>
#include <services/gap/ble_svc_gap.h>
#include <services/gatt/ble_svc_gatt.h>
#include "../config.h"
#include "internal.h"

extern "C" void ble_store_config_init(void);  // NimBLE's key store (RAM only: no bonding here)

static const char* TAG = "ble";

namespace hostlink {
namespace detail {

namespace {

constexpr uint16_t kNoConn = 0xFFFF;
// 515, not 517: NimBLE caps an attribute at 512 bytes and rejects a longer write, and the app
// chunks its writes to MTU - 3. With 515 a full chunk is exactly 512.
constexpr uint16_t kMtu = 515;

enum BleEvent : uint8_t { EV_CONNECTED, EV_SUBSCRIBED, EV_DISCONNECTED };

ble_uuid128_t s_svcUuid, s_rxUuid, s_txUuid;
ble_gatt_chr_def s_chrs[3] = {};
ble_gatt_svc_def s_svcs[2] = {};
uint16_t s_txHandle = 0;
uint8_t s_ownAddrType = BLE_OWN_ADDR_PUBLIC;
char s_name[32] = "Puck";

StreamBufferHandle_t s_rx = nullptr;
QueueHandle_t s_events = nullptr;
std::atomic<uint16_t> s_conn{kNoConn};
std::atomic<bool> s_subscribed{false};

char s_line[LINK_MAX_LINE + 1];
size_t s_len = 0;
bool s_overflow = false;

void pushEvent(BleEvent e) {
  uint8_t v = e;
  xQueueSend(s_events, &v, 0);
}

// "60bd0001-b6ce-41b2-9911-a4a7e64e01d9" → NimBLE's 128-bit UUID (bytes little-endian)
bool parseUuid(const char* s, ble_uuid128_t* out) {
  uint8_t be[16];
  int n = 0;
  for (const char* p = s; *p && n < 32; p++) {
    if (*p == '-') continue;
    int v = (*p >= '0' && *p <= '9') ? *p - '0' : (*p >= 'a' && *p <= 'f') ? *p - 'a' + 10 : (*p >= 'A' && *p <= 'F') ? *p - 'A' + 10 : -1;
    if (v < 0) return false;
    if (n % 2 == 0) be[n / 2] = (uint8_t)(v << 4);
    else be[n / 2] |= (uint8_t)v;
    n++;
  }
  if (n != 32) return false;
  out->u.type = BLE_UUID_TYPE_128;
  for (int i = 0; i < 16; i++) out->value[i] = be[15 - i];
  return true;
}

int gapEvent(struct ble_gap_event* event, void* arg);

void advertise() {
  // The 128-bit service UUID fills most of the 31-byte advert, so the name goes in the scan response.
  ble_hs_adv_fields adv = {};
  adv.flags = BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP;
  adv.uuids128 = &s_svcUuid;
  adv.num_uuids128 = 1;
  adv.uuids128_is_complete = 1;
  int rc = ble_gap_adv_set_fields(&adv);
  if (rc != 0) ESP_LOGE(TAG, "advert fields: %d", rc);

  ble_hs_adv_fields rsp = {};
  rsp.name = (const uint8_t*)s_name;
  rsp.name_len = (uint8_t)strlen(s_name);
  rsp.name_is_complete = 1;
  rc = ble_gap_adv_rsp_set_fields(&rsp);
  if (rc != 0) ESP_LOGE(TAG, "scan response fields: %d", rc);

  ble_gap_adv_params params = {};
  params.conn_mode = BLE_GAP_CONN_MODE_UND;
  params.disc_mode = BLE_GAP_DISC_MODE_GEN;
  params.itvl_min = 160;  // 100–150 ms: found quickly without costing much battery
  params.itvl_max = 240;
  rc = ble_gap_adv_start(s_ownAddrType, nullptr, BLE_HS_FOREVER, &params, gapEvent, nullptr);
  if (rc != 0 && rc != BLE_HS_EALREADY) ESP_LOGE(TAG, "advertising failed: %d", rc);
}

int gapEvent(struct ble_gap_event* event, void*) {
  switch (event->type) {
    case BLE_GAP_EVENT_CONNECT:
      if (event->connect.status != 0) {
        advertise();
        break;
      }
      s_conn.store(event->connect.conn_handle);
      s_subscribed.store(false);
      {
        // 7.5–15 ms interval, no latency, 4 s supervision timeout: the pointer needs the short
        // interval. Windows may settle on something longer; that still works, just less smoothly.
        ble_gap_upd_params p = {};
        p.itvl_min = 6;
        p.itvl_max = 12;
        p.latency = 0;
        p.supervision_timeout = 400;
        p.min_ce_len = BLE_GAP_INITIAL_CONN_MIN_CE_LEN;
        p.max_ce_len = BLE_GAP_INITIAL_CONN_MAX_CE_LEN;
        ble_gap_update_params(event->connect.conn_handle, &p);
      }
      pushEvent(EV_CONNECTED);
      break;

    case BLE_GAP_EVENT_DISCONNECT:
      if (event->disconnect.conn.conn_handle == s_conn.load()) {
        s_conn.store(kNoConn);
        s_subscribed.store(false);
        pushEvent(EV_DISCONNECTED);
      }
      advertise();
      break;

    case BLE_GAP_EVENT_ADV_COMPLETE:
      if (s_conn.load() == kNoConn) advertise();
      break;

    case BLE_GAP_EVENT_SUBSCRIBE:
      if (event->subscribe.attr_handle == s_txHandle && event->subscribe.conn_handle == s_conn.load()) {
        const bool on = event->subscribe.cur_notify != 0;
        s_subscribed.store(on);
        if (on) pushEvent(EV_SUBSCRIBED);
      }
      break;

    case BLE_GAP_EVENT_MTU:
      ESP_LOGI(TAG, "MTU %u", event->mtu.value);
      break;

    default:
      break;
  }
  return 0;
}

// The app writes into RX; TX has no readable value (notify only).
int accessChr(uint16_t, uint16_t, struct ble_gatt_access_ctxt* ctxt, void*) {
  if (ctxt->op != BLE_GATT_ACCESS_OP_WRITE_CHR) return BLE_ATT_ERR_UNLIKELY;
  uint8_t buf[512];
  uint16_t len = 0;
  if (ble_hs_mbuf_to_flat(ctxt->om, buf, sizeof(buf), &len) != 0) return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
  if (len) xStreamBufferSend(s_rx, buf, len, 0);  // full: dropped, the line fails to parse
  return 0;
}

void onSync() {
  int rc = ble_hs_util_ensure_addr(0);
  if (rc == 0) rc = ble_hs_id_infer_auto(0, &s_ownAddrType);
  if (rc != 0) {
    ESP_LOGE(TAG, "no usable address: %d", rc);
    return;
  }
  advertise();
}

void onReset(int reason) { ESP_LOGW(TAG, "host reset: %d", reason); }

void hostTask(void*) {
  nimble_port_run();  // returns only after nimble_port_stop()
  nimble_port_freertos_deinit();
}

void feed(const uint8_t* data, size_t n) {
  for (size_t i = 0; i < n; i++) {
    char c = (char)data[i];
    if (c == '\n') {
      if (!s_overflow && s_len) {
        s_line[s_len] = 0;
        onLine(TP_BLE, s_line, s_len);
      }
      s_len = 0;
      s_overflow = false;
    } else if (c == '\r') {
    } else if (s_len < LINK_MAX_LINE) {
      s_line[s_len++] = c;
    } else {
      s_overflow = true;
    }
  }
}

}  // namespace

void bleBegin(const char* name) {
  s_rx = xStreamBufferCreate(4096, 1);
  s_events = xQueueCreate(16, sizeof(uint8_t));
  copyText(s_name, sizeof(s_name), name);

  if (!parseUuid(BLE_SERVICE_UUID, &s_svcUuid) || !parseUuid(BLE_RX_UUID, &s_rxUuid) ||
      !parseUuid(BLE_TX_UUID, &s_txUuid)) {
    ESP_LOGE(TAG, "bad UUID in config.h");
    return;
  }
  esp_err_t err = nimble_port_init();
  if (err != ESP_OK) {
    ESP_LOGE(TAG, "NimBLE init failed: %s", esp_err_to_name(err));
    return;
  }
  esp_ble_tx_power_set(ESP_BLE_PWR_TYPE_DEFAULT, ESP_PWR_LVL_P9);
  esp_ble_tx_power_set(ESP_BLE_PWR_TYPE_ADV, ESP_PWR_LVL_P9);

  ble_hs_cfg.sync_cb = onSync;
  ble_hs_cfg.reset_cb = onReset;
  ble_hs_cfg.sm_bonding = 0;  // the app pairs at its own level; no BLE bonding

  s_chrs[0].uuid = &s_rxUuid.u;
  s_chrs[0].access_cb = accessChr;
  s_chrs[0].flags = BLE_GATT_CHR_F_WRITE | BLE_GATT_CHR_F_WRITE_NO_RSP;
  s_chrs[1].uuid = &s_txUuid.u;
  s_chrs[1].access_cb = accessChr;
  s_chrs[1].flags = BLE_GATT_CHR_F_NOTIFY;
  s_chrs[1].val_handle = &s_txHandle;
  s_svcs[0].type = BLE_GATT_SVC_TYPE_PRIMARY;
  s_svcs[0].uuid = &s_svcUuid.u;
  s_svcs[0].characteristics = s_chrs;

  ble_svc_gap_init();
  ble_svc_gatt_init();
  int rc = ble_gatts_count_cfg(s_svcs);
  if (rc == 0) rc = ble_gatts_add_svcs(s_svcs);
  if (rc != 0) ESP_LOGE(TAG, "GATT service: %d", rc);
  ble_svc_gap_device_name_set(s_name);
  ble_att_set_preferred_mtu(kMtu);
  ble_store_config_init();

  nimble_port_freertos_init(hostTask);  // the host task, pinned to core 0 (sdkconfig)
}

void blePoll() {
  if (!s_events) return;
  // Events first, so a disconnect is seen before bytes from a newer connection.
  uint8_t e;
  while (xQueueReceive(s_events, &e, 0) == pdTRUE) {
    switch (e) {
      case EV_CONNECTED:
        s_len = 0;
        s_overflow = false;
        onBleConnected();
        break;
      case EV_SUBSCRIBED:
        onBleSubscribed();
        break;
      case EV_DISCONNECTED:
        xStreamBufferReset(s_rx);
        s_len = 0;
        s_overflow = false;
        onBleDisconnected();
        break;
    }
  }
  uint8_t chunk[128];
  for (int budget = 4096; budget > 0;) {
    size_t got = xStreamBufferReceive(s_rx, chunk, sizeof(chunk), 0);
    if (!got) break;
    budget -= (int)got;
    feed(chunk, got);
  }
}

bool bleConnected() { return s_conn.load() != kNoConn; }
bool bleSubscribed() { return bleConnected() && s_subscribed.load(); }

size_t bleMaxChunk() {
  uint16_t conn = s_conn.load();
  if (conn == kNoConn) return 20;
  uint16_t mtu = ble_att_mtu(conn);
  return mtu > 23 ? mtu - 3 : 20;
}

int bleNotify(const uint8_t* data, size_t len) {
  uint16_t conn = s_conn.load();
  if (conn == kNoConn || !s_txHandle) return -1;
  // A lost piece would corrupt the byte stream, so running out of buffers is reported (and the
  // TX task retries the same bytes) rather than dropped.
  os_mbuf* om = ble_hs_mbuf_from_flat(data, len);
  if (!om) return 1;
  int rc = ble_gatts_notify_custom(conn, s_txHandle, om);  // consumes om either way
  if (rc == 0) return 0;
  if (rc == BLE_HS_ENOMEM || rc == BLE_HS_EBUSY || rc == BLE_HS_ENOMEM_EVT) return 1;
  return -1;
}

void bleDisconnect() {
  uint16_t conn = s_conn.load();
  if (conn != kNoConn) ble_gap_terminate(conn, BLE_ERR_REM_USER_CONN_TERM);
}

}  // namespace detail
}  // namespace hostlink
