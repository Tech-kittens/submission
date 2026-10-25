# Speaker setup against firmware main

Frontend contract checked against QuacktionHW commit `17e7492`.

1. Hold K2 for three seconds and release to enter setup. Firmware also opens setup
   when Wi-Fi or backend credentials are missing. Holding K2 for ten seconds is
   factory reset; the app does not ask users to do this for normal configuration.
2. Scan for the Quacktion BLE service and connect using Security 2. Read
   `device.info` for the stable identity; BLE addresses can change.
3. Briefly press K2 to approve this connection. Poll `setup.status` until
   `approval` is `approved`. Approval is required again after reconnecting.
4. Register/pair the speaker through the signed-in account's existing backend
   device routes. Send the returned device ID and one-time credential over BLE
   with `backend.configure`. Never send the account access token to the speaker.
5. Use `wifi.scan` and `wifi.configure`, or `wifi.dpp.start` with Android's Easy
   Connect flow on supported phones. A successful Android return alone does not
   mean the speaker joined Wi-Fi; poll firmware status.
6. Continue polling until Wi-Fi is `connected` and backend is `online`. Only then
   enable Finish setup, which calls `setup.complete`. The speaker reboots into
   normal mode with Bluetooth off. Cancel setup explicitly calls `setup.cancel`.

The app remembers the stable speaker identity and backend device ID, not a BLE
pairing credential. Old saved records with an `appToken` still load, but that
obsolete field is ignored and is not transmitted. Forget saved speaker removes
only this phone's record. Removing backend ownership remains a separate action
in the account device list.

There is no `device.pair`, `device.unpair`, `setup.close`, mDNS wake endpoint,
HTTP configuration service, or automatic background BLE reconnect in this flow.
Later changes require physically opening setup with K2. `wifi.forget` removes
Wi-Fi configuration while leaving the current BLE setup connection available.

The firmware source stash does not change the binary already installed on the
speaker. Rebuild and flash the committed firmware separately before verifying
this exact contract on hardware.
