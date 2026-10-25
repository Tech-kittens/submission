import { randomBytes } from "node:crypto";
import { NextRequest, NextResponse } from "next/server";
import { appUrl, redirectUri } from "@/lib/app-url";
import { open, seal } from "@/lib/crypto";
import { env } from "@/lib/env";
import { cookieName, cookiePath, type McpAuthRequest, type SavedAuth } from "@/lib/oauth";
import { PROVIDER_LABELS, SERVICE_LABELS, SERVICE_PATH, SERVICE_SCOPES, type Provider, type Service } from "@/lib/scopes";
import { exchangeCode, firstSite, jiraClient, type JiraCredential } from "@/lib/jira";
import { mintToken, type JiraRefresh } from "@/lib/token";

export const esc = (s: string) => s.replace(/[&<>"]/g, (c) => `&#${c.charCodeAt(0)};`);

export const page = (body: string, status = 200) =>
  new Response(
    `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>MCP</title>
<style>
body{font:16px/1.5 system-ui,sans-serif;max-width:46rem;margin:3rem auto;padding:0 1rem;color:#1a1a1a}
pre,code{background:#f3f3f3;border-radius:6px;font-size:13px}pre{padding:.8rem;overflow:auto}code{padding:.1rem .3rem}
.token{word-break:break-all;padding:.8rem;display:block}button{font:inherit;padding:.4rem .9rem;cursor:pointer}
@media(prefers-color-scheme:dark){body{background:#161616;color:#eee}pre,code{background:#262626}}
</style>${body}`,
    { status, headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" } },
  );

function authUrl(provider: Provider, services: Service[], state: string, req: Request): URL {
  const scopes = services.flatMap((s) => SERVICE_SCOPES[s]);
  if (provider === "google") {
    const url = new URL("https://accounts.google.com/o/oauth2/v2/auth");
    url.search = new URLSearchParams({
      client_id: env("GOOGLE_CLIENT_ID"),
      redirect_uri: redirectUri(req, provider),
      response_type: "code",
      scope: ["openid", "email", ...scopes].join(" "),
      access_type: "offline", // we need a refresh token
      prompt: "consent", // Google only returns a refresh token on consent
      state,
    }).toString();
    return url;
  }
  if (provider === "jira") {
    const url = new URL("https://auth.atlassian.com/authorize");
    url.search = new URLSearchParams({
      audience: "api.atlassian.com",
      client_id: env("ATLASSIAN_CLIENT_ID"),
      scope: scopes.join(" "),
      redirect_uri: redirectUri(req, provider),
      state,
      response_type: "code",
      prompt: "consent",
    }).toString();
    return url;
  }
  const url = new URL("https://github.com/login/oauth/authorize");
  url.search = new URLSearchParams({
    client_id: env("GITHUB_CLIENT_ID"),
    redirect_uri: redirectUri(req, provider),
    scope: scopes.join(" "),
    state,
  }).toString();
  return url;
}

/** Redirect the user to Google/GitHub. `mcp` is set when Claude.ai (an MCP client) started the flow. */
export function beginSignIn(req: Request, provider: Provider, services: Service[], mcp?: McpAuthRequest) {
  const state = randomBytes(16).toString("base64url");
  const saved: SavedAuth = { state, services, ...(mcp ? { mcp } : {}) };
  const res = NextResponse.redirect(authUrl(provider, services, state, req));
  res.cookies.set(cookieName(provider), seal(saved), {
    httpOnly: true,
    sameSite: "lax",
    secure: process.env.NODE_ENV === "production",
    path: cookiePath(provider),
    maxAge: 600,
  });
  return res;
}

type Exchange = { credential: string; id: string; refresh?: Omit<JiraRefresh, "k"> } | { error: string };

async function exchange(provider: Provider, code: string, req: Request, services: Service[]): Promise<Exchange> {
  if (provider === "jira") {
    try {
      const t = await exchangeCode(code, redirectUri(req, provider));
      const { cloudId, site } = await firstSite(t.accessToken);
      const cred: JiraCredential = { at: t.accessToken, exp: t.exp, cloudId, site };
      const me = await jiraClient(cred)
        .request<{ displayName?: string }>("GET", "/rest/api/3/myself")
        .catch(() => ({}) as { displayName?: string });
      const id = `${me.displayName ?? "unknown"} @ ${new URL(site).host}`;
      return { credential: JSON.stringify(cred), id, refresh: { rt: t.refreshToken, cloudId, site, id, services } };
    } catch (e) {
      return { error: e instanceof Error ? e.message : "Token exchange failed" };
    }
  }
  if (provider === "google") {
    const tokenRes = await fetch("https://oauth2.googleapis.com/token", {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        code,
        client_id: env("GOOGLE_CLIENT_ID"),
        client_secret: env("GOOGLE_CLIENT_SECRET"),
        redirect_uri: redirectUri(req, provider),
        grant_type: "authorization_code",
      }),
    });
    const tokens = (await tokenRes.json().catch(() => ({}))) as {
      access_token?: string;
      refresh_token?: string;
      error_description?: string;
    };
    if (!tokenRes.ok || !tokens.access_token) return { error: tokens.error_description ?? "Token exchange failed" };
    if (!tokens.refresh_token) {
      return {
        error: "No refresh token returned. Remove this app at myaccount.google.com/permissions and connect again.",
      };
    }
    const info = (await (
      await fetch("https://openidconnect.googleapis.com/v1/userinfo", {
        headers: { authorization: `Bearer ${tokens.access_token}` },
      })
    ).json()) as { email?: string };
    return { credential: tokens.refresh_token, id: info.email ?? "unknown" };
  }

  const tokenRes = await fetch("https://github.com/login/oauth/access_token", {
    method: "POST",
    headers: { accept: "application/json", "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      client_id: env("GITHUB_CLIENT_ID"),
      client_secret: env("GITHUB_CLIENT_SECRET"),
      code,
      redirect_uri: redirectUri(req, provider),
    }),
  });
  // GitHub returns HTTP 200 even on errors, so check the body.
  const tokens = (await tokenRes.json().catch(() => ({}))) as {
    access_token?: string;
    error?: string;
    error_description?: string;
  };
  if (!tokens.access_token) return { error: tokens.error_description ?? tokens.error ?? "Token exchange failed" };
  const me = await fetch("https://api.github.com/user", {
    headers: {
      authorization: `Bearer ${tokens.access_token}`,
      accept: "application/vnd.github+json",
      "user-agent": "google-github-mcp",
    },
  });
  const info = (await me.json().catch(() => ({}))) as { login?: string };
  return { credential: tokens.access_token, id: info.login ?? "unknown" };
}

/** Shared OAuth callback for both providers. */
export async function handleCallback(req: NextRequest, provider: Provider): Promise<Response> {
  const label = PROVIDER_LABELS[provider];
  const clear = `${cookieName(provider)}=; Path=${cookiePath(provider)}; Max-Age=0`;
  const code = req.nextUrl.searchParams.get("code");
  const state = req.nextUrl.searchParams.get("state");
  const error = req.nextUrl.searchParams.get("error");
  if (error) return page(`<h1>${label} declined</h1><p>${esc(error)}</p><p><a href="/">Try again</a></p>`, 400);

  const saved = open<SavedAuth>(req.cookies.get(cookieName(provider))?.value ?? "");
  if (!code || !saved || saved.state !== state) {
    return page(`<h1>Invalid or expired sign-in</h1><p><a href="/">Start again</a></p>`, 400);
  }

  if (provider === "jira" && !saved.mcp) {
    return page(
      `<h1>Jira connects through an MCP client</h1><p>Atlassian rotates refresh tokens, so Jira can't use a copy-and-paste bearer token. Add the Jira URL as a connector in Claude.ai (or use <code>/mcp</code> in Claude Code) and sign in from there.</p>`,
      400,
    );
  }
  const result = await exchange(provider, code, req, saved.services);
  if ("error" in result) return page(`<h1>Sign-in failed</h1><p>${esc(result.error)}</p><p><a href="/">Try again</a></p>`, 400);

  const payload = { provider, credential: result.credential, id: result.id, services: saved.services };

  // Sign-in started by an MCP client (Claude.ai): hand back a short-lived sealed code.
  // The bearer itself is minted at /oauth/token (so Jira can also return a refresh token there).
  if (saved.mcp) {
    const { client_id, redirect_uri, state: clientState, code_challenge } = saved.mcp;
    const authCode = seal({ payload, refresh: result.refresh, client_id, redirect_uri, state: clientState, code_challenge, exp: Date.now() + 5 * 60_000 });
    const target = new URL(redirect_uri);
    target.searchParams.set("code", authCode);
    if (clientState) target.searchParams.set("state", clientState);
    const redirect = NextResponse.redirect(target);
    redirect.headers.append("set-cookie", clear);
    return redirect;
  }

  // Sign-in started from the home page: show the bearer token for Claude Code / other clients.
  const bearer = mintToken(payload);
  const base = appUrl(req);
  const endpoints = saved.services
    .map(
      (s) => `<h3>${SERVICE_LABELS[s]}</h3><pre>claude mcp add --transport http ${s} ${esc(`${base}${SERVICE_PATH[s]}/mcp`)} \\
  --header "Authorization: Bearer $MCP_TOKEN"</pre>`,
    )
    .join("");
  const json = JSON.stringify(
    {
      mcpServers: Object.fromEntries(
        saved.services.map((s) => [
          s,
          { type: "http", url: `${base}${SERVICE_PATH[s]}/mcp`, headers: { Authorization: "Bearer <YOUR_TOKEN>" } },
        ]),
      ),
    },
    null,
    2,
  );
  const res = page(`<h1>Connected as ${esc(result.id)}</h1>
<p>Your bearer token (shown once, treat it like a password):</p>
<code class="token" id="t">${bearer}</code>
<p><button onclick="navigator.clipboard.writeText(document.getElementById('t').textContent)">Copy token</button></p>
<h2>Claude Code</h2><pre>export MCP_TOKEN=&lt;token&gt;</pre>${endpoints}
<h2>Other clients (JSON config)</h2><pre>${esc(json)}</pre>`);
  res.headers.append("set-cookie", clear);
  return res;
}
