# Mobile developer handoff: return to Quacktion after MCP authorization

## What the backend already does

The app starts account connection by calling `POST /api/mcp/connections/{provider}/connect` with the signed-in user's bearer token. The response contains `authorizationUrl`. Open that URL in the system browser or an Android Custom Tab.

After authorization, the provider returns to the backend at `https://quackback.ofneill.com/api/mcp/oauth/callback`. On success, the backend redirects the browser to **`https://quackback.ofneill.com/mobile/mcp-complete`**. This second URL is the app's Android App Link. The app must handle this URL; it must never handle the API callback, OAuth `code`, `state`, or provider tokens.

If the app does not open, `/mobile/mcp-complete` displays a public fallback page telling the user to return to Quacktion manually. The page carries no account information. A failure in the backend callback does not redirect to the success link.

## Android implementation

1. Register an HTTPS intent filter for the **exact host and path** on the activity that can restore the connection screen. Use the actual app package and activity names. Do not register `/api/mcp/oauth/callback`.

   ```xml
   <intent-filter android:autoVerify="true">
       <action android:name="android.intent.action.VIEW" />
       <category android:name="android.intent.category.DEFAULT" />
       <category android:name="android.intent.category.BROWSABLE" />
       <data android:scheme="https"
             android:host="quackback.ofneill.com"
             android:path="/mobile/mcp-complete" />
   </intent-filter>
   ```

2. Handle the incoming `Intent.data` when the app starts cold **and** when an existing activity receives a new intent. For a reused activity, implement `onNewIntent` and update the activity's current intent as appropriate. Route the user to the connections screen after the app has restored authentication. Avoid opening a duplicate screen when the link and activity resume both fire.

3. Start authorization from the app with its authenticated API call, then open `authorizationUrl` in a Custom Tab or system browser. Keep enough local UI state to show that a connection is pending when the app resumes. Do not use an embedded WebView or construct the provider URL yourself.

4. When the completion link arrives, call `GET /api/mcp/connections` with the user's bearer token and update the UI from that response. The link means the browser reached the completion URL; it is **not proof** that a particular account is connected. Refresh the connection list on ordinary app resume too, since some browsers may show the fallback page instead of opening the app. A short, bounded retry is reasonable while the backend and UI settle; stop after a few attempts and let the user retry manually.

5. If authentication has expired, refresh it using the normal app flow before requesting connections. Do not put auth tokens or account identifiers in the App Link URL. Treat repeated links and resumes as one refresh operation where possible.

## Domain association and Railway

The backend serves `https://quackback.ofneill.com/.well-known/assetlinks.json`. It must name the installed app's real package and SHA-256 **signing certificate** fingerprint. The backend has checked-in values for known builds, but deployment should be checked against the build users actually install. For a Play-distributed app, use the Play app-signing certificate fingerprint.

The deployment operator can override association values in Railway:

```text
AndroidAppLinks__PackageName=<actual Android application ID>
AndroidAppLinks__Sha256CertFingerprints__0=<colon-separated SHA-256 signing certificate fingerprint>
```

Additional intentionally trusted certificates use `__1`, `__2`, and so on. There is **no Railway variable for the app-return destination**: the backend currently redirects to the fixed `/mobile/mcp-complete` URL. `Mcp__OAuthCallbackUrl` configures the *provider-to-backend* callback, not the *browser-to-app* return.

## Verify on an installed build

1. Fetch `https://quackback.ofneill.com/.well-known/assetlinks.json` and confirm the package name and fingerprint match the installed build.
2. Check Android's domain-verification state for `quackback.ofneill.com`, then open `https://quackback.ofneill.com/mobile/mcp-complete` from a browser. Confirm it opens the app in both cold and warm states. If it stays in the browser, check the manifest filter, signing fingerprint, and domain verification before changing the OAuth flow.
3. Complete a real MCP connection. Confirm the app returns to the connections screen and shows the connection **only after** `GET /api/mcp/connections` reports it.
4. Repeat with the app already open, with expired app authentication, and with the app link disabled so the fallback page appears. Confirm manual return and app resume still refresh connections.
5. Check that callback query parameters, provider tokens, and authorization URLs are absent from app analytics and error logs.

For the broader connection and approval APIs, see [MOBILE-MCP-INTEGRATION.md](MOBILE-MCP-INTEGRATION.md).
