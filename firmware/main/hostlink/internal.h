// Shared between the hostlink files; not part of the public interface (see ../hostlink.h).
//
// Threads:
//   main loop    hostlink::poll(): reads both transports, runs the protocol, writes g_model
//   NimBLE host  BLE callbacks (ble_link.cpp, on the NimBLE host task): only push bytes / events into FreeRTOS buffers for poll()
//   link TX task drains the outgoing queue to USB and BLE, coalesces mouse traffic
//   input task   (hal) calls sendMove/sendScroll/sendClick/sendButton directly
#pragma once
#include <stddef.h>
#include <stdint.h>

namespace hostlink {
namespace detail {

// Transports, as bits: where an outgoing line may go.
enum : uint8_t {
  TP_USB = 0x01,
  TP_BLE = 0x02,
  TP_ALL = TP_USB | TP_BLE,
};

// Routing flags for an outgoing line.
enum : uint8_t {
  F_TRUSTED = 0x10,  // only to transports whose host is paired
  F_FORCE = 0x20,    // even if the transport isn't known to be up (the boot hello on USB)
};

// ---- tx.cpp: the outgoing queue and the TX task
void txBegin();
// Queues one line (no trailing '\n'). targets: TP_* bits (0 = all). Returns false if dropped.
bool txLine(const char* line, size_t len, uint8_t targets, uint8_t flags);
void txSetAlive(uint8_t tpMask);    // transports that are up
void txSetTrusted(uint8_t tpMask);  // transports whose host is paired
uint8_t txTrusted();
void txAddMove(char src, float x, float y);
void txAddScroll(float y, float x);
void txSetVolume(int v);
void txFlushMouse();                // queue pending moves/scrolls now (keeps them ahead of a click)

// ---- usb_link.cpp
void usbBegin();
void usbPoll();                                // complete lines → onLine(TP_USB, ...)
size_t usbWrite(const char* data, size_t len);  // never blocks; bytes accepted

// ---- ble_link.cpp
void bleBegin(const char* name);
void blePoll();                                // events → onBle*(), lines → onLine(TP_BLE, ...)
bool bleConnected();
bool bleSubscribed();
size_t bleMaxChunk();                          // ATT MTU - 3
// 0 = sent, 1 = out of buffers (try the same bytes again later), -1 = not connected (drop)
int bleNotify(const uint8_t* data, size_t len);
void bleDisconnect();

// ---- hostlink.cpp: the protocol, called from poll() (main loop)
void onLine(uint8_t tp, char* line, size_t len);
void onBleConnected();
void onBleSubscribed();
void onBleDisconnected();

// ---- store.cpp: NVS ("puck" namespace)
void storeBegin();
bool storeHasHost(const char* id);
void storeAddHost(const char* id, const char* name);  // adds or refreshes; evicts the oldest
bool storeRemoveHost(const char* id);
void storeLoadCfg();      // applies the saved cfg to g_model and hal
void storeSaveCfgSoon();  // debounced
void storePoll();

// UTF-8-safe bounded copy (never cuts a multi-byte character in half). Always NUL-terminates.
void copyText(char* dst, size_t size, const char* src);

}  // namespace detail
}  // namespace hostlink
