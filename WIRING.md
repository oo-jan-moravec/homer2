# Homer2 Rover – Wiring Reference

Pin assignments for the Raspberry Pi and Arduino in the rover setup.

---

## Raspberry Pi (rpi-rover-brain4)

Target: Pi Zero 2 W or similar. Uses **BCM GPIO** numbering.

### GPIO Pins

| GPIO | Physical | Function              | Notes                                |
|------|----------|-----------------------|--------------------------------------|
| 2    | 3        | I2C SDA1              | LCD (PCF8574), shared I2C bus       |
| 3    | 5        | I2C SCL1              | LCD (PCF8574), shared I2C bus       |
| 14   | 8        | UART TXD               | Serial TX → Arduino RX               |
| 15   | 10       | UART RXD               | Serial RX ← Arduino TX               |
| 23   | 16       | IR LED                 | Output, drives IR illuminator        |
| 24   | 18       | Ultrasound ECHO        | Input, HC-SR04 echo (test-suite)     |
| 25   | 22       | Ultrasound TRIG        | Output, HC-SR04 trigger (test-suite) |
| 26   | 37       | Heartbeat LED          | Output, status blink                  |

### I2C Bus 1 (LCD)

| Device      | Address | Function                          |
|-------------|---------|-----------------------------------|
| PCF8574     | 0x27    | I2C→parallel, drives HD44780 LCD   |

Config: `appsettings.json` → `Rover:Lcd:BusId` (default 1), `Rover:Lcd:Address` (default 0x27).

### UART (Serial)

| Pin    | Role | Connection         |
|--------|------|--------------------|
| GPIO 14| TX   | → Arduino RX (D0)  |
| GPIO 15| RX   | ← Arduino TX (D1)  |

Baud: 115200 8N1. Port: `/dev/serial0` (configurable via `Rover:SerialPort`).

### Other Connectors

- **CSI** – Pi Camera (rpicam-still / libcamera-vid)
- **USB** – USB sound card (arecord/aplay, plughw:1,0)
- **Power** – 5V supply

---

## Arduino (homer2_v8, Uno-compatible)

Uses **digital/analog pin** names (Dn, An).

### Power / Analog

| Pin | Function       | Circuit                         |
|-----|----------------|----------------------------------|
| A0  | Battery sense  | Voltage divider: R1=47k, R2=22k, Vin → R1 → R2 → GND |

### Motors (TB6612FNG)

| Arduino | TB6612 | Motor |
|---------|--------|-------|
| D5      | PWMA   | Left (A) PWM  |
| D8      | AIN1   | Left direction |
| D12     | AIN2   | Left direction |
| D11     | PWMB   | Right (B) PWM |
| D7      | BIN1   | Right direction |
| D13     | BIN2   | Right direction |

### Encoders (quadrature)

| Arduino | Function    |
|---------|-------------|
| D9      | Left encoder (PCINT)  |
| D6      | Right encoder (PCINT) |

### Serial (to Pi)

| Pin | Role | Connection      |
|-----|------|-----------------|
| D0  | RX   | ← Pi GPIO 14 TX |
| D1  | TX   | → Pi GPIO 15 RX |

---

## Connection Summary

```
Pi GPIO 14 (TXD) ──────► Arduino D0 (RX)
Pi GPIO 15 (RXD) ◄────── Arduino D1 (TX)
Pi GPIO 23 ───────────► IR LED (via driver if needed)
Pi GPIO 26 ───────────► Heartbeat LED
Pi GPIO 2/3 (I2C) ────► PCF8574 → HD44780 LCD
```

---

## Notes

1. **I2C pull-ups** – Most PCF8574/LCD boards include pull-ups. For long cables or noise, consider reducing I2C speed (`dtparam=i2c_arm_baudrate=10000` in `/boot/config.txt`).
2. **Serial level** – Pi UART is 3.3V. Arduino Uno RX can accept 3.3V; do not drive Pi RX from 5V Arduino TX without a level shifter.
3. **Ultrasound** – HC-SR04 (GPIO 24/25) is used by the test-suite only, not by the main rover backend.
