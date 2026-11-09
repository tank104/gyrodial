// Board pins, addresses and constants for the Waveshare ESP32-S3-Touch-AMOLED-1.75C.
// Pin numbers come from Waveshare's board_config/pin_config.h for this board.
#pragma once
#include <stdint.h>

// The firmware version is PROJECT_VER in firmware/CMakeLists.txt (esp_app_get_description()).
#define GYRODIAL_PROTOCOL_VERSION 1

// ---- display: CO5300 AMOLED, 466x466, QSPI
#define LCD_W 466
#define LCD_H 466
#define LCD_COL_OFFSET 6  // the panel's visible area starts 6 columns in (Waveshare uses 6,0)
#define LCD_ROW_OFFSET 0
#define PIN_LCD_CS 12
#define PIN_LCD_SCLK 38
#define PIN_LCD_D0 4
#define PIN_LCD_D1 5
#define PIN_LCD_D2 6
#define PIN_LCD_D3 7
#define PIN_LCD_RST 1
#define LCD_QSPI_HZ 80000000

// ---- shared I2C bus: touch, IMU, PMU, audio codecs
#define PIN_I2C_SDA 15
#define PIN_I2C_SCL 14
#define I2C_HZ 400000
#define I2C_ADDR_TOUCH 0x5A   // CST9217
#define I2C_ADDR_IMU 0x6B     // QMI8658 (SA0 high: QMI8658_L_SLAVE_ADDRESS)
#define I2C_ADDR_PMU 0x34     // AXP2101
#define I2C_ADDR_ES8311 0x18  // speaker codec
#define I2C_ADDR_ES7210 0x40  // mic ADC

// ---- touch
#define PIN_TP_INT 11
#define PIN_TP_RST 2
#define TOUCH_MIRROR_X true   // as Waveshare's LVGL example: setMirrorXY(true, true)
#define TOUCH_MIRROR_Y true

// ---- audio (I2S shared by ES8311 out and ES7210 in)
#define PIN_I2S_MCLK 16
#define PIN_I2S_BCLK 9
#define PIN_I2S_LRCK 45
#define PIN_I2S_DOUT 8   // to ES8311 (speaker)
#define PIN_I2S_DIN 10   // from ES7210 (mics)
#define PIN_PA_EN 46     // speaker amplifier enable, active high

// ---- buttons
#define PIN_BOOT_BTN 0   // BOOT, active low. The PWR key is read through the AXP2101.

// ---- motion: which way is "clockwise" when the dial lies screen-up on the desk.
// QMI8658 gyro z is counter-clockwise-positive seen from above (right-hand rule, z out of the
// screen), so clockwise = -gz. Flip here if the board turns out to be mounted the other way.
#define TURN_SIGN (-1.0f)

// ---- Bluetooth LE (see docs/PROTOCOL.md)
#define BLE_SERVICE_UUID "60bd0001-b6ce-41b2-9911-a4a7e64e01d9"
#define BLE_RX_UUID      "60bd0002-b6ce-41b2-9911-a4a7e64e01d9"
#define BLE_TX_UUID      "60bd0003-b6ce-41b2-9911-a4a7e64e01d9"

// ---- protocol limits
#define LINK_MAX_LINE 1024
#define LINK_HOST_TIMEOUT_MS 10000
#define PAIR_MAX_HOSTS 4

// ---- palette (RGB888), shared by every face
#define COL_BG      0x000000
#define COL_YELLOW  0xFFD60A
#define COL_WHITE   0xF5F5F7
#define COL_GREY1   0x8E8E93
#define COL_GREY2   0x5A5A5E
#define COL_GREY3   0x2C2C2E
#define COL_GREY4   0x1C1C1E
#define COL_RED     0xFF453A
#define COL_GREEN   0x32D74B
#define COL_CLAUDE  0xD97757
