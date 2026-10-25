# Fix MCP OAuth return-to-app verification

## Finding

The hosted flow reaches the “Account authorization complete” browser page, but Android has not verified the completion link for the installed Quacktion build.

Checked on 25 October 2026:

- Application ID: `com.example.quacktion_frontend`
- Completion URL: `https://quackback.ofneill.com/mobile/mcp-complete`
- Association URL: `https://quackback.ofneill.com/.well-known/assetlinks.json`
- Association response: HTTP 200, JSON, without a redirect.
- Android domain verification state: `1024` (a device-verifier error, not verified).
- The installed app's signing certificate is absent from the hosted association file.

The certificate mismatch is a concrete verification blocker. The error number alone does not identify its cause; the mismatch was established by comparing the installed app signature with the hosted JSON.

## Requested backend / hosting change

Add this SHA-256 certificate fingerprint to the existing association for `com.example.quacktion_frontend`:

```text
EB:34:7A:2F:8C:50:1A:1B:57:B3:54:57:4A:9A:1A:C4:48:47:BD:D3:0D:29:C2:53:F2:A1:B3:75:CE:19:B7:06
```

This is the certificate for the local test build currently installed on the connected phone. It is not a proposed production signing identity. Only keep this certificate trusted for as long as this development build is intentionally supported; use the actual release / Play app-signing certificate for production.

The following JSON preserves the currently hosted fingerprint and adds the local test build's fingerprint. Confirm that the existing identity should remain trusted before deploying:

```json
[
  {
    "relation": [
      "delegate_permission/common.handle_all_urls"
    ],
    "target": {
      "namespace": "android_app",
      "package_name": "com.example.quacktion_frontend",
      "sha256_cert_fingerprints": [
        "1A:8A:2C:E4:85:15:F2:BD:15:A8:24:CD:10:89:B9:A3:B8:A7:A7:3A:61:46:05:F2:9D:65:F7:82:6F:2C:E3:F5",
        "EB:34:7A:2F:8C:50:1A:1B:57:B3:54:57:4A:9A:1A:C4:48:47:BD:D3:0D:29:C2:53:F2:A1:B3:75:CE:19:B7:06"
      ]
    }
  }
]
```

A standalone copy is included alongside this document as `assetlinks.local-testing.json`.

Serve this content at exactly:

`https://quackback.ofneill.com/.well-known/assetlinks.json`

It must remain publicly accessible over HTTPS, without authentication or redirects, and served as JSON.

## Keep the OAuth routing contract

- Provider/gateway OAuth responses must continue reaching `/api/mcp/oauth/callback` on the backend.
- After successful validation, code exchange, and connection persistence, redirect to `/mobile/mcp-complete`.
- The app claims only `/mobile/mcp-complete`; do not make it intercept the backend OAuth callback.
- Keep the completion HTML page as a fallback when navigation stays in the browser.
- Do not put provider tokens, OAuth codes, or OAuth state in the completion link. The app confirms account status with authenticated `GET /api/mcp/connections`.

## Verification after deployment

First check the public file contains the installed build's fingerprint:

```bash
curl -i https://quackback.ofneill.com/.well-known/assetlinks.json
```

On the frontend developer's machine with the test phone connected:

```bash
adb shell pm verify-app-links --re-verify com.example.quacktion_frontend
adb shell pm get-app-links --user cur com.example.quacktion_frontend
```

Verification is asynchronous; wait for it to complete before checking again. The expected domain state is:

```text
quackback.ofneill.com: verified
```

Then test normal Android URL resolution, without forcing an activity or package:

```bash
adb shell am start -W \
  -a android.intent.action.VIEW \
  -c android.intent.category.BROWSABLE \
  -d 'https://quackback.ofneill.com/mobile/mcp-complete'
```

Expected: Android opens Quacktion and the app shows Connections after restoring authentication. If signed out, it shows sign-in first and opens Connections afterward.

Finally repeat a real provider authorization from the app's Custom Tab. A successful ADB URL test does not by itself prove the browser's OAuth redirect will hand off correctly. If the domain is verified but Samsung Browser still stays on the fallback page, inspect the actual redirect destination and browser link-opening behavior as a separate issue.

No forced verification or manual domain approval has been applied to the phone to hide this mismatch.

## Requested confirmation

Please confirm when the updated association file is deployed so the frontend team can re-run verification and test the complete OAuth return flow.

## Reference

[Android App Link verification](https://developer.android.com/training/app-links/verify-applinks)
