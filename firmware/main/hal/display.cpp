// CO5300 AMOLED (466x466) over QSPI, on ESP-IDF's spi_master.
//
// The panel takes two kinds of transfer, each one CS-low window:
//   register write: opcode 0x02, 24-bit address (cmd << 8), then the parameters, all on one line
//   pixel write:    opcode 0x32, 24-bit address 0x002C00 (RAMWR) on one line, pixels on four lines
// Pixels go to the window last set with CASET/RASET. A pixel stream longer than one SPI
// transaction (the S3 caps a transaction at 2^18 bits) continues with plain data phases while CS
// is held low (SPI_TRANS_CS_KEEP_ACTIVE).
#include <string.h>
#include <driver/gpio.h>
#include <driver/spi_master.h>
#include <esp_attr.h>
#include <esp_heap_caps.h>
#include "../config.h"
#include "hal_internal.h"

static const char* TAG = "hal";

namespace hal {
namespace detail {

namespace {

// The vendor BSP for this board runs the panel at 40 MHz. The pins aren't the SPI2 IOMUX pins, so
// the signals go through the GPIO matrix; config.h's 80 MHz may work but is unverified.
constexpr int kLcdClockHz = 40 * 1000 * 1000;
constexpr spi_host_device_t kHost = SPI2_HOST;
constexpr size_t kMaxChunk = 32000;  // bytes per transaction, under the 32 KiB hardware cap
constexpr int kQueueDepth = 10;

constexpr uint8_t kOpWriteReg = 0x02;
constexpr uint8_t kOpWritePixels = 0x32;
constexpr uint32_t kAddrRamwr = 0x002C00;

constexpr uint8_t CMD_SLPIN = 0x10, CMD_SLPOUT = 0x11, CMD_DISPOFF = 0x28, CMD_DISPON = 0x29,
                  CMD_CASET = 0x2A, CMD_RASET = 0x2B, CMD_TEON = 0x35, CMD_PIXFMT = 0x3A,
                  CMD_BRIGHTNESS = 0x51, CMD_CTRL1 = 0x53, CMD_CE = 0x58, CMD_HBM = 0x63,
                  CMD_SPIMODE = 0xC4, CMD_PAGE = 0xFE;

spi_device_handle_t s_dev = nullptr;
spi_transaction_ext_t s_pool[kQueueDepth];
int s_poolNext = 0;
volatile int s_inFlight = 0;  // queued, result not collected yet

struct DoneCb {
  void (*fn)(void*);
  void* arg;
};
DoneCb s_done[kQueueDepth];

void postCb(spi_transaction_t* t) {
  auto* cb = static_cast<DoneCb*>(t->user);
  if (cb && cb->fn) cb->fn(cb->arg);
}

void collectOne() {
  spi_transaction_t* done = nullptr;
  if (spi_device_get_trans_result(s_dev, &done, portMAX_DELAY) == ESP_OK) s_inFlight = s_inFlight - 1;
}

// The pool is a ring as deep as the driver's queue: if every slot is in flight, the oldest must be
// collected before its slot can be reused. Returns a zeroed transaction whose post-callback slot
// is cleared.
spi_transaction_ext_t* nextSlot(DoneCb** cb) {
  if (s_inFlight >= kQueueDepth) collectOne();
  const int i = s_poolNext;
  s_poolNext = (s_poolNext + 1) % kQueueDepth;
  spi_transaction_ext_t* t = &s_pool[i];
  memset(t, 0, sizeof(*t));
  s_done[i] = {nullptr, nullptr};
  t->base.user = &s_done[i];
  *cb = &s_done[i];
  return t;
}

}  // namespace

void lcdDrain() {
  if (!s_dev) return;
  while (s_inFlight > 0) collectOne();
}

void lcdCommand(uint8_t cmd, const uint8_t* params, size_t len) {
  if (!s_dev) return;
  lcdDrain();
  spi_transaction_ext_t t;
  memset(&t, 0, sizeof(t));
  t.base.cmd = kOpWriteReg;
  t.base.addr = (uint32_t)cmd << 8;
  if (len > 0 && len <= 4) {
    t.base.flags = SPI_TRANS_USE_TXDATA;
    memcpy(t.base.tx_data, params, len);
  } else if (len > 4) {
    t.base.tx_buffer = params;  // callers keep long parameter lists in RAM
  }
  t.base.length = len * 8;
  spi_device_polling_transmit(s_dev, &t.base);
}

static void cmd1(uint8_t cmd, uint8_t v) { lcdCommand(cmd, &v, 1); }

void lcdSetWindow(int x1, int y1, int x2, int y2) {
  x1 += LCD_COL_OFFSET;
  x2 += LCD_COL_OFFSET;
  y1 += LCD_ROW_OFFSET;
  y2 += LCD_ROW_OFFSET;
  const uint8_t cols[4] = {(uint8_t)(x1 >> 8), (uint8_t)x1, (uint8_t)(x2 >> 8), (uint8_t)x2};
  const uint8_t rows[4] = {(uint8_t)(y1 >> 8), (uint8_t)y1, (uint8_t)(y2 >> 8), (uint8_t)y2};
  lcdCommand(CMD_CASET, cols, 4);
  lcdCommand(CMD_RASET, rows, 4);
}

bool lcdQueuePixels(const void* data, size_t bytes, void (*onDone)(void*), void* arg) {
  if (!s_dev || bytes == 0) {
    if (onDone) onDone(arg);
    return s_dev != nullptr;
  }
  const uint8_t* p = static_cast<const uint8_t*>(data);
  bool first = true;
  while (bytes > 0) {
    size_t n = bytes > kMaxChunk ? kMaxChunk : bytes;
    bool last = n == bytes;
    DoneCb* cb = nullptr;
    spi_transaction_ext_t* t = nextSlot(&cb);
    t->base.flags = SPI_TRANS_MODE_QIO;
    if (first) {
      t->base.cmd = kOpWritePixels;
      t->base.addr = kAddrRamwr;
    } else {
      // continuation: no command or address phase, CS still low from the previous chunk
      t->base.flags |= SPI_TRANS_VARIABLE_CMD | SPI_TRANS_VARIABLE_ADDR;
      t->command_bits = 0;
      t->address_bits = 0;
    }
    if (!last) t->base.flags |= SPI_TRANS_CS_KEEP_ACTIVE;
    t->base.tx_buffer = p;
    t->base.length = n * 8;
    if (last) *cb = {onDone, arg};
    if (spi_device_queue_trans(s_dev, &t->base, portMAX_DELAY) != ESP_OK) return false;
    s_inFlight = s_inFlight + 1;
    p += n;
    bytes -= n;
    first = false;
  }
  return true;
}

void lcdBrightness(uint8_t value) { cmd1(CMD_BRIGHTNESS, value); }

void lcdSleep(bool sleep) {
  if (sleep) {
    lcdCommand(CMD_DISPOFF, nullptr, 0);
    lcdCommand(CMD_SLPIN, nullptr, 0);
    sleepMs(120);
  } else {
    lcdCommand(CMD_SLPOUT, nullptr, 0);
    sleepMs(120);  // the panel ignores DISPON until sleep-out has settled
  }
}

// Fill the whole panel RAM with black, a band at a time, so nothing random shows at power-on.
static void clearPanel(uint8_t* scratch, size_t scratchBytes) {
  memset(scratch, 0, scratchBytes);
  const int rowsPerBand = (int)(scratchBytes / (LCD_W * 2)) & ~1;  // even, so bands stay 2-px aligned
  for (int y = 0; y < LCD_H; y += rowsPerBand) {
    int y2 = y + rowsPerBand - 1;
    if (y2 >= LCD_H) y2 = LCD_H - 1;
    lcdSetWindow(0, y, LCD_W - 1, y2);
    lcdQueuePixels(scratch, (size_t)LCD_W * (y2 - y + 1) * 2, nullptr, nullptr);
    lcdDrain();
  }
}

bool lcdBegin() {
  spi_bus_config_t bus;
  memset(&bus, 0, sizeof(bus));
  bus.sclk_io_num = PIN_LCD_SCLK;
  bus.data0_io_num = PIN_LCD_D0;
  bus.data1_io_num = PIN_LCD_D1;
  bus.data2_io_num = PIN_LCD_D2;
  bus.data3_io_num = PIN_LCD_D3;
  bus.data4_io_num = -1;
  bus.data5_io_num = -1;
  bus.data6_io_num = -1;
  bus.data7_io_num = -1;
  bus.max_transfer_sz = kMaxChunk + 16;
  bus.flags = SPICOMMON_BUSFLAG_MASTER | SPICOMMON_BUSFLAG_QUAD;
  if (spi_bus_initialize(kHost, &bus, SPI_DMA_CH_AUTO) != ESP_OK) {
    ESP_LOGE(TAG, "display: SPI bus init failed");
    return false;
  }

  spi_device_interface_config_t dev;
  memset(&dev, 0, sizeof(dev));
  dev.command_bits = 8;
  dev.address_bits = 24;
  dev.mode = 0;
  dev.clock_speed_hz = kLcdClockHz;
  dev.spics_io_num = PIN_LCD_CS;
  dev.flags = SPI_DEVICE_HALFDUPLEX;
  dev.queue_size = kQueueDepth;
  dev.post_cb = postCb;
  if (spi_bus_add_device(kHost, &dev, &s_dev) != ESP_OK) {
    ESP_LOGE(TAG, "display: SPI device add failed");
    s_dev = nullptr;
    return false;
  }
  // The panel is the only device on this bus. Holding the bus lets multi-chunk pixel streams keep
  // CS low between transactions.
  spi_device_acquire_bus(s_dev, portMAX_DELAY);

  gpio_reset_pin((gpio_num_t)PIN_LCD_RST);
  gpio_set_direction((gpio_num_t)PIN_LCD_RST, GPIO_MODE_OUTPUT);
  gpio_set_level((gpio_num_t)PIN_LCD_RST, 1);
  sleepMs(10);
  gpio_set_level((gpio_num_t)PIN_LCD_RST, 0);
  sleepMs(20);
  gpio_set_level((gpio_num_t)PIN_LCD_RST, 1);
  sleepMs(120);

  // Init sequence for this board's panel, as in Waveshare's ESP-IDF BSP for the 1.75C
  // (vendor page 0x20 settings first, then the standard page).
  cmd1(CMD_PAGE, 0x20);
  cmd1(0x19, 0x10);
  cmd1(0x1C, 0xA0);
  cmd1(CMD_PAGE, 0x00);
  cmd1(CMD_SPIMODE, 0x80);
  cmd1(CMD_PIXFMT, 0x55);      // RGB565
  cmd1(CMD_TEON, 0x00);
  cmd1(CMD_CTRL1, 0x20);       // brightness control on
  cmd1(CMD_BRIGHTNESS, 0x00);  // dark until the first frame is in
  cmd1(CMD_HBM, 0xFF);
  cmd1(CMD_CE, 0x00);          // contrast enhancement off
  lcdCommand(CMD_SLPOUT, nullptr, 0);
  sleepMs(120);

  // Clear the panel RAM before it's switched on. Borrow a DMA buffer just for this.
  const size_t scratchBytes = (size_t)LCD_W * 34 * 2;
  uint8_t* scratch = (uint8_t*)heap_caps_malloc(scratchBytes, MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL);
  if (scratch) {
    clearPanel(scratch, scratchBytes);
    heap_caps_free(scratch);
  }
  lcdCommand(CMD_DISPON, nullptr, 0);
  sleepMs(10);
  return true;
}

}  // namespace detail
}  // namespace hal
