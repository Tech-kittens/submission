#pragma once

/* Runs BLE setup mode as described in docs/ble-protocol.md. Never returns: the duck reboots into
 * normal mode on completion, cancel or timeout. Must not run in a boot that started ESP-SR. */
void setup_run(void);
