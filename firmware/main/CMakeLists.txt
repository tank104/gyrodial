idf_component_register(
    SRC_DIRS "." "hal" "hostlink" "ui" "ui/fonts"
    INCLUDE_DIRS "."
    REQUIRES
        driver esp_driver_gpio esp_driver_spi esp_driver_i2c esp_driver_i2s esp_driver_usb_serial_jtag
        esp_app_format esp_timer esp_system esp_hw_support esp_psram nvs_flash bt json freertos log efuse
)
target_compile_options(${COMPONENT_LIB} PRIVATE -Wno-missing-field-initializers)
