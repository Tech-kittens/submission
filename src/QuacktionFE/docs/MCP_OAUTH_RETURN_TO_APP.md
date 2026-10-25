# MCP authorization: return to Quacktion

Implementation handoff. This document proposes changes; it does not mean they have been implemented.

Frontend status: Custom Tabs, the exact completion intent filter, native cold/warm
link delivery, sign-in deferral, and authenticated refresh/navigation are now
implemented. Backend redirects, the fallback page, and the hosted
`assetlinks.json` remain deployment work; until then, manual return still works.

## Goal

Open MCP account authorization in an Android Custom Tab, then return to Quacktion's connections page after the backend saves the connection. Keep provider credentials and OAuth code exchange entirely on the backend.

## Current implementation

Verified against backend `main` at `5bb1782`.

- `POST /api/mcp/connections/{serverId}/connect` returns an `authorizationUrl`.
- The frontend opens it through `SystemAuthBrowser` and the Android `quacktion/external_auth` method channel, currently using `ACTION_VIEW`.
- The gateway redirects to `https://quackback.ofneill.com/api/mcp/oauth/callback`.
- `McpController.Callback` calls `CompleteConnectAsync`, then returns the connection as JSON in the browser.
- The frontend refreshes connections when resumed and uses bounded polling while authorization is pending. There is no registered Android completion App Link.

Relevant files, relative to their respective repository roots:

| Repository | File | Responsibility |
| --- | --- | --- |
| Backend | `src/Quacktion.Web/Controllers/McpController.cs` | OAuth callback response |
| Backend | `src/Quacktion.Data/Agents/McpConnectionService.cs` | State, PKCE, code exchange, connection persistence |
| Backend | `docs/MOBILE-MCP-INTEGRATION.md` | Existing mobile API contract |
| Frontend | `lib/features/integrations/data/external_auth_browser.dart` | Browser abstraction |
| Frontend | `lib/features/integrations/application/integrations_controller.dart` | Connection refresh and pending authorization |
| Frontend | `android/app/src/main/kotlin/com/example/quacktion_frontend/MainActivity.kt` | Native browser launch |
| Frontend | `android/app/src/main/AndroidManifest.xml` | Activity and link registration |
| Frontend | `android/app/build.gradle.kts` | Application ID, signing, browser dependency |

## Intended flow

1. User taps Connect in Quacktion.
2. App requests the authorization URL using its existing Quacktion bearer token.
3. App opens the URL in an Android Custom Tab.
4. User approves access at the provider.
5. Gateway sends the OAuth response to the existing backend callback.
6. Backend validates state, exchanges the code, and persists the connection.
7. Only after persistence succeeds, backend issues an HTTP 302 redirect to:
   `https://quackback.ofneill.com/mobile/mcp-complete`
8. Android routes that verified App Link to Quacktion.
9. App opens the connections page and fetches `GET /api/mcp/connections` with the current user's bearer token.
10. App displays the confirmed connection and clears the pending authorization UI.

The completion link is a navigation signal, not proof of authorization. Anyone can open it; only the authenticated connections response establishes which accounts are connected.

## Backend work

### Change the successful callback response

Keep the provider/gateway callback URL unchanged. Do not redirect the gateway directly to the app: the backend still needs to consume the code and validate OAuth state.

After `CompleteConnectAsync` succeeds, return a redirect instead of JSON:

```csharp
await service.CompleteConnectAsync(code, state, ct);
return Redirect("https://quackback.ofneill.com/mobile/mcp-complete");
```

This illustrates the successful branch only. Retain existing callback input validation, state expiry, single-use state protection, PKCE, and server-side credential storage. Do not redirect to the successful completion URL if the exchange or persistence fails.

Use a fixed destination, or a trusted configuration value validated at startup. Do not accept an arbitrary `returnUrl` query parameter. If web and mobile callers later need different destinations, bind an allowlisted destination identifier to the server-side OAuth transaction when beginning authorization.

### Provide a browser fallback page

Serve `GET /mobile/mcp-complete` as a small public HTML page for cases where the app is absent, link verification fails, or the browser keeps the navigation on the web:

> Account authorization complete. Return to Quacktion to view your connections.

Do not require an API bearer token to render this page. Do not display account details, connection objects, provider credentials, OAuth codes, or state values. Do not assume a browser can close its own tab automatically.

### Handle failures explicitly

- Initially, preserve existing safe callback errors; only successful callbacks need to redirect.
- If adding a failure return flow, use a separate allowlisted completion outcome such as `?outcome=cancelled` or `?outcome=failed`. Treat it as a display hint only.
- Never forward raw provider error descriptions, callback code/state, or tokens into an app link.
- Callback and proxy access logs must redact sensitive OAuth query values, including failed callbacks.

### Preserve existing clients

Check whether any other clients depend on the callback's JSON body before replacing it. If necessary, store a mobile completion mode in the OAuth transaction at connect time and retain JSON behavior for other callers. The Android app currently refreshes connections rather than consuming this JSON body.

## Domain / deployment work

Host this file at exactly:

`https://quackback.ofneill.com/.well-known/assetlinks.json`

Example, with placeholders that must be replaced before deployment:

```json
[
  {
    "relation": ["delegate_permission/common.handle_all_urls"],
    "target": {
      "namespace": "android_app",
      "package_name": "com.example.quacktion_frontend",
      "sha256_cert_fingerprints": [
        "REPLACE_WITH_ACTUAL_APP_SIGNING_CERTIFICATE_SHA256"
      ]
    }
  }
]
```

- Serve valid JSON over HTTPS, without authentication or redirects, with an appropriate JSON content type.
- The application ID is currently `com.example.quacktion_frontend`. Update the association if that changes.
- Use the certificate that signs the installed APK. For Play distribution, use the Play app-signing certificate, not the upload certificate.
- Local debug builds and production builds may use different certificates. Prefer a test domain for debug associations; only publish intentionally trusted signing identities.
- Run `./gradlew signingReport` from the frontend's `android` directory to inspect local certificate fingerprints.
- The backend hosting layer or reverse proxy may serve this file; it does not have to be a controller action.

## Android / Flutter work

### Register only the completion link

Add this intent filter to the existing `MainActivity`, alongside the launcher filter:

```xml
<intent-filter android:autoVerify="true">
    <action android:name="android.intent.action.VIEW" />
    <category android:name="android.intent.category.DEFAULT" />
    <category android:name="android.intent.category.BROWSABLE" />
    <data
        android:scheme="https"
        android:host="quackback.ofneill.com"
        android:path="/mobile/mcp-complete" />
</intent-filter>
```

Do not claim `/api/mcp/oauth/callback` in the app manifest. That endpoint must reach the backend so it can complete authorization.

### Handle both cold and warm app launches

Choose one link delivery mechanism: a Flutter App Link plugin or native intent handling forwarded through a platform channel. Avoid duplicate delivery from competing Flutter and plugin handlers.

- Handle the initial launch URL and subsequent links while `MainActivity` is alive (`singleTop` is already configured).
- Match the exact HTTPS scheme, host, and completion path; ignore unrelated URLs.
- Queue navigation until Flutter initialization and restored authentication are ready.
- If signed out, show sign-in and refresh connections after authentication. Never attach a connection to an account based on the URL.
- Navigate to the existing connections page without creating repeated copies of it.
- Immediately trigger the existing connection refresh, including after a cold start where no pending connect operation exists in memory.
- Confirm success from the API before showing a connected toast. Do not require a new connection ID: reauthorizing an existing account may update its existing connection.
- Deduplicate link/resume refreshes and keep bounded polling/resume refresh as fallback.
- On transient API failure, offer retry; do not restart OAuth automatically.
- Avoid resetting an ongoing voice conversation when navigating back to connections.

### Use a Custom Tab for authorization

Keep the existing `ExternalAuthBrowser` interface. Replace the native plain `ACTION_VIEW` launch with AndroidX Browser `CustomTabsIntent`, or an equivalent maintained Flutter integration supporting browser-backed tabs.

- Add a compatible stable AndroidX Browser dependency if implementing this natively.
- Retain existing HTTPS URL validation and a normal browser fallback if Custom Tabs are unavailable.
- Do not use an embedded WebView for provider login.
- Keep the browser launch result separate from authorization completion: opening or closing a tab does not prove that the connection succeeded.
- Verify the real callback-to-App-Link transition on the target phone/browser. If it remains in the browser, show the completion fallback and retain manual return/resume refresh. Do not assume a Custom Tab guarantees automatic return in every browser.

## Verification

Backend tests:

- Successful exchange redirects to the fixed completion URL only after saving the connection.
- Invalid, expired, replayed, or missing state cannot create a connection or reach the success branch.
- Provider rejection and exchange failures never redirect as successful authorization.
- An attacker-supplied return URL cannot change the redirect destination.
- Callback responses, completion URLs/pages, and application logs do not expose secrets.

Frontend tests:

- Valid completion links refresh authenticated connections and navigate once.
- Wrong hosts/paths/schemes are ignored.
- Duplicate link and resume events do not duplicate requests or success messages.
- Cold start, signed-out return, existing-account reauthorization, cancellation, timeout, and refresh failure behave correctly.

On-device checks after deploying the association file and installing the updated app:

```bash
adb shell pm verify-app-links --re-verify com.example.quacktion_frontend
adb shell pm get-app-links com.example.quacktion_frontend
adb shell am start -W -a android.intent.action.VIEW \
  -c android.intent.category.BROWSABLE \
  -d 'https://quackback.ofneill.com/mobile/mcp-complete'
```

Check verification status before testing; verification is asynchronous. Test with the app foregrounded, backgrounded, and terminated. Also test without the app installed to confirm the browser fallback works.

Finally perform a real Google authorization through the Custom Tab. Confirm that the backend saves the connection, the browser returns to Quacktion, and the app confirms it using the connections API. Opening the link with ADB alone does not test the full OAuth flow.

## Suggested rollout order

1. Deploy the association file and browser fallback page.
2. Implement and test frontend link handling and Custom Tabs.
3. Deploy the backend success redirect.
4. Test the complete hosted flow and update `MOBILE-MCP-INTEGRATION.md` to describe the new callback behavior.

No firmware changes are needed.

## References

- [Android App Links](https://developer.android.com/training/app-links/about)
- [Android App Link verification](https://developer.android.com/training/app-links/verify-android-applinks)
- [Android Custom Tabs](https://developer.chrome.com/docs/android/custom-tabs)
- [OAuth for native apps: embedded user agents](https://www.rfc-editor.org/rfc/rfc8252.html#section-8.12)
