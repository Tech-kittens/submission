Below is a handoff you can give the firmware agent. The BLE protocol and setup behavior described here are proposed requirements; they are not implemented in the current firmware. The existing firmware initializes the board, microphone level meter, boot sound, and LEDs.

Objective

Implement speaker onboarding for the Waveshare ESP32-S3 AI Speaker using ESP-IDF 6.1:

\- Discover and identify the speaker using Bluetooth LE.

\- Establish an authenticated setup connection.

\- Configure Wi-Fi through either Android Wi-Fi Easy Connect/DPP or manual credentials over BLE.

\- Install the existing backend device credential securely.

\- Report setup progress and failures to the app.

\- Resume normal operation after reboot.

Bluetooth is the setup transport. It does not need to remain connected during normal speaker use.

1\. Entering setup mode

Use a deliberate physical action, such as holding an available button for three seconds. Verify the board’s actual button wiring before selecting the button.

Proposed behavior:

\- An unconfigured speaker enters setup mode on first boot.

\- A configured speaker enters setup mode only after the physical action.

\- Advertise for a limited period, such as five minutes.

\- Indicate setup mode with a distinct LED pattern.

\- Exit on completion, cancellation, or timeout.

\- Re-entering setup mode must not automatically erase existing Wi-Fi or account credentials.

Changing Wi-Fi and factory reset must be separate actions.

2\. How the app identifies the correct Bluetooth device

There are three separate questions:

Question	Mechanism

Is this advertising the Quacktion setup protocol?	Advertised BLE service UUID

Which nearby speaker did the user select?	Device identifier, short name, and physical identification

Can we trust this setup connection with credentials?	Authenticated provisioning session





A device name alone does not answer all three.

Discovery

Define a Quacktion provisioning service with a dedicated 128-bit UUID. Generate and document the UUID once; firmware and frontend must use exactly the same value.

Advertise:

\- The provisioning service UUID.

\- A short local name, for example Quacktion-A1B2.

\- Compact service data containing protocol version and setup availability, if space allows.

The app filters by the service UUID. The name helps the user distinguish results; it is not an authentication mechanism.

Do not put Wi-Fi credentials, backend credentials, account identifiers, or the full DPP URI into advertisements.

Stable identity

Provide a readable device-information endpoint with a response resembling:

{

&#x20; "protocolVersion": 1,

&#x20; "deviceIdentifier": "<stable-device-identifier>",

&#x20; "model": "waveshare-esp32-s3-ai-speaker",

&#x20; "firmwareVersion": "0.2.0",

&#x20; "setupState": "awaiting\_authorization",

&#x20; "capabilities": {

&#x20;   "manualWifi": true,

&#x20;   "dpp": true,

&#x20;   "identify": true

&#x20; }

}

These fields are a proposed contract.

deviceIdentifier should remain stable across reboots and Wi-Fi changes. Document whether factory reset preserves it; preserving hardware identity is preferable.

Do not use the phone’s BLE peripheral identifier or the current BLE address as the permanent backend identity. Keep these concepts separate:

\- Physical device identifier.

\- Backend-assigned deviceId.

\- BLE connection/address.

\- Short display suffix.

Neither a serial number nor a MAC-derived identifier proves authenticity.

Physical identification

Add an identify operation that causes the selected speaker to flash a recognizable pattern and play a short sound.

The app can display:

“Is this the speaker that flashed and quacked?”



This prevents accidental selection when several speakers are nearby. Rate-limit this operation so nearby clients cannot continuously trigger it.

Physical identification helps the user select the intended unit, but it does not by itself cryptographically authenticate the BLE connection.

3\. Authenticate the setup connection

Use Espressif’s provisioning security rather than inventing an encryption protocol. Its provisioning framework supports BLE transport, custom endpoints, and authenticated security schemes, including Security 2 using SRP6a and AES-GCM. Espressif provisioning documentation

A proposed approach that requires no QR code:

1\. The user physically enables setup mode.

2\. The speaker generates a temporary random setup code.

3\. The speaker speaks the code using locally stored digit audio.

4\. The user enters it in the app.

5\. The code is used with the supported authenticated provisioning scheme.

6\. Only after successful authentication can the app change Wi-Fi or install account credentials.

Implementation requirements:

\- Use a fresh code for each setup window.

\- Expire the code when the window closes.

\- Limit failed authentication attempts.

\- Do not expose the code through BLE advertisements or unauthenticated reads.

\- Permit only one authorized setup session at a time.

\- Never use a universal hardcoded setup password.

This establishes possession of the nearby speaker under the assumption that the spoken code is received by the intended user. It is not manufacturer attestation. Proving that a device is genuine Quacktion hardware would require a separately designed certificate/key provisioning system.

Also, distinguish BLE connection, BLE bonding, and backend account pairing. They are different operations. The app should not require users to pair through Android’s general Bluetooth settings unless the chosen security implementation specifically needs it.

4\. Wi-Fi option A: Share Wi-Fi using Easy Connect/DPP

The user sees Share Wi-Fi from phone when the phone and firmware support it.

The flow is:

1\. Establish the authenticated BLE setup session.

2\. The app requests DPP setup.

3\. Firmware initializes DPP enrollee mode, generates bootstrap information, and starts listening.

4\. Firmware returns its DPP URI over the authenticated BLE connection.

5\. The app passes that URI to Android’s system Easy Connect flow.

6\. Android performs DPP provisioning with the speaker.

7\. Firmware receives the network configuration and attempts to connect.

8\. Firmware reports connection progress and the final result.

Android supports obtaining the URI through BLE and invoking ACTION\_PROCESS\_WIFI\_EASY\_CONNECT\_URI. The app must check WifiManager.isEasyConnectSupported() before offering this path. Android handles the network selection and credential sharing; our app does not retrieve the saved Wi-Fi password. Android Easy Connect documentation

For firmware, use the checked-out ESP-IDF 6.1 DPP enrollee example as the implementation reference. Relevant APIs/events include:

esp\_supp\_dpp\_init(...)

esp\_supp\_dpp\_bootstrap\_gen(...)

ESP\_SUPP\_DPP\_URI\_READY

esp\_supp\_dpp\_start\_listen()

ESP\_SUPP\_DPP\_CFG\_RECVD

ESP\_SUPP\_DPP\_FAIL

esp\_supp\_dpp\_stop\_listen()

Inspect the 6.1 headers and example for exact signatures and event payloads. Do not copy an older example’s configuration-event handling without checking it.

The “QR code” bootstrap mode generates the URI that a QR code would normally encode. Our design transfers that URI over BLE instead of rendering a QR image. This integration still needs testing on the actual Android phone.

Additional requirements:

\- Keep the DPP private bootstrap key on the speaker.

\- Keep the URI/key valid throughout the active attempt.

\- Cancel listening and invalidate the attempt when setup expires.

\- Stop DPP before switching to manual provisioning.

\- Do not interpret Android returning to our app as proof the speaker connected.

\- Preserve BLE status reporting while Wi-Fi provisioning runs.

\- Test Wi-Fi/BLE coexistence on the actual board.

5\. Wi-Fi option B: Enter network details over BLE

This is the fallback when DPP is unavailable, fails, or the user chooses manual setup.

The flow is:

1\. The app requests a Wi-Fi scan.

2\. The speaker scans networks it can actually use.

3\. Firmware returns network names, signal strength, and authentication types.

4\. The user chooses a network and enters its password.

5\. The app sends the configuration through the authenticated, encrypted provisioning session.

6\. Firmware attempts the connection.

7\. Firmware reports success or an actionable failure.

Support manual SSID entry for hidden networks. Initially, support explicitly defined personal-network authentication modes; do not claim support for enterprise authentication or captive-portal onboarding.

The ESP32-S3 uses 2.4 GHz Wi-Fi, so the UI should explain that requirement.

Credential handling:

\- Validate SSID/password lengths according to the supported Wi-Fi authentication mode.

\- Never echo passwords in responses.

\- Never log passwords.

\- Do not return saved passwords through a read endpoint.

\- Keep the previous working configuration until the replacement has been tested.

\- Persist successful configuration so it survives reboot.

A failed Wi-Fi change should leave the user able to retry or return to the previous network.

6\. Proposed app–firmware operations

Use the provisioning framework’s existing Wi-Fi operations where suitable. Add custom endpoints for Quacktion-specific behavior.

These names describe proposed logical operations, not existing GATT characteristic names:

Operation	Purpose

device.info	Read identity, protocol version, and capabilities

device.identify	Flash/play identification feedback

setup.status	Read the current setup snapshot

wifi.scan	List available networks

wifi.configure	Submit manual network configuration

wifi.dpp.start	Begin DPP and return its URI

wifi.dpp.cancel	Stop an active DPP attempt

backend.configure	Install backend device identity and credential

setup.complete	Finish setup and close provisioning





Before implementing the frontend counterpart, publish:

\- Service and characteristic UUIDs.

\- Encoding and field definitions.

\- Authentication requirements for each operation.

\- Message-size limits and fragmentation behavior.

\- Request identifiers and retry behavior.

\- Stable error codes.

Do not assume a DPP URI, scan result, or credential payload fits in one BLE packet. Use the provisioning transport’s supported framing and test with a small negotiated MTU.

7\. Progress and recovery

Expose Wi-Fi and backend state separately. For example:

{

&#x20; "protocolVersion": 1,

&#x20; "setupSessionId": "<setup-session>",

&#x20; "requestId": "<operation-id>",

&#x20; "wifiState": "connected",

&#x20; "backendState": "connecting",

&#x20; "ssid": "Home Wi-Fi",

&#x20; "errorCode": null

}

Suggested states:

Wi-Fi state	Meaning

unconfigured	No saved network

scanning	Discovering networks

awaiting\_dpp	Waiting for DPP provisioning

connecting	Joining the network

connected	Obtained an IP address

failed	Attempt failed





Backend states can be unlinked, connecting, online, or failed.

Important distinctions:

\- Obtaining an IP address does not prove backend connectivity.

\- An unreachable backend does not mean the Wi-Fi password is wrong.

\- A backend credential rejection is an account-linking problem.

Provide a readable status snapshot even if notifications are used. After the app reconnects, it must be able to recover the current state without restarting provisioning.

Repeated requests should not create overlapping scans, DPP listeners, or connection attempts.

8\. Linking the speaker to the existing backend

The existing backend already has a pairing flow:

1\. The app creates a device with deviceIdentifier, name, and firmwareVersion.

2\. The backend returns the device record and a pairing challenge.

3\. The app submits that challenge to /api/devices/{id}/pair.

4\. The backend returns deviceId and a one-time plaintext device credential.

5\. The app transfers that credential to the speaker through the authenticated setup channel.

6\. Firmware stores it and acknowledges successful persistence.

The backend challenge confirms the backend pairing transaction. It does not authenticate the physical BLE device. That is why the setup authentication described earlier matters.

The credential must pass transiently through the app for this existing flow, but should not remain in frontend logs or ordinary app storage. Never send the account’s access/refresh tokens or an ElevenLabs key to the speaker.

The current backend documents device connectivity through:

GET /ws/devices/{deviceId}

Authorization: Device <credential>

Sec-WebSocket-Protocol: quacktion.v1

Implement the corresponding control protocol if using that connection to establish “online” status. Verify the app’s device-status endpoint reports the connection before displaying Ready.

These behaviors are documented in the local \[backend authentication contract (line 152)](/home/grace/Documents/Codex/Quacktion/QuacktionBE/AUTH.md:152) and \[device protocol](/home/grace/Documents/Codex/Quacktion/QuacktionBE/docs/device-protocol.md).

If credential installation fails, the backend cannot return the same plaintext credential again. The app must follow the documented unpair/re-pair recovery flow rather than pretending setup succeeded.

A speaker connecting directly to the backend cannot use the phone’s localhost address or adb reverse. Development setup needs a backend address reachable from the speaker’s Wi-Fi network.

9\. Completion and reset behavior

Show Ready only after:

\- Wi-Fi configuration has been saved successfully.

\- The backend device credential has been stored.

\- The speaker has authenticated to the backend.

\- The app has confirmed the corresponding device is online.

Then close the provisioning session and stop setup advertising.

For production, protect persisted secrets with the appropriate encrypted storage and device-security configuration. Do not enable irreversible eFuse settings as an incidental development step.

Factory reset may erase local network/account credentials, but it must not imply that backend ownership has been transferred. Backend unpairing and ownership changes remain separate operations.

10\. Acceptance tests for the firmware agent

Test at least:

\- Two speakers nearby: selecting and identifying one affects only that speaker.

\- A fake device using the same advertised name cannot bypass setup authentication.

\- Incorrect/expired setup codes are rejected and attempts are limited.

\- DPP succeeds on a compatible Android phone.

\- DPP cancellation/failure permits manual setup.

\- Correct and incorrect manual passwords produce appropriate status.

\- Hidden-network setup works for supported authentication modes.

\- BLE disconnect/reconnect recovers status without restarting the operation.

\- Power loss during provisioning does not corrupt the previous working configuration.

\- Wi-Fi success plus backend failure is reported accurately.

\- Backend credential installation survives reboot.

\- Wi-Fi changes preserve account linking.

\- Repeated requests do not duplicate provisioning operations.

\- Normal boot reconnects without reopening unrestricted setup mode.

The key distinction for the agent is: service UUIDs discover candidates, physical feedback identifies the selected speaker, and authenticated provisioning protects the credentials sent to it.

