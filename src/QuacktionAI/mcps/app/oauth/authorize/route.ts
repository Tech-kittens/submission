import { NextRequest, NextResponse } from "next/server";
import { open } from "@/lib/crypto";
import { serviceFromResource, type ClientInfo } from "@/lib/oauth";
import { beginSignIn, esc, page } from "@/lib/signin";
import { PROVIDERS, PROVIDER_LABELS, SERVICE_PROVIDER, isProvider, servicesOf, type Provider } from "@/lib/scopes";

export const runtime = "nodejs";

export async function GET(req: NextRequest) {
  const q = req.nextUrl.searchParams;
  const clientId = q.get("client_id") ?? "";
  const client = open<ClientInfo>(clientId);
  const redirect = q.get("redirect_uri") ?? client?.redirect_uris[0];
  if (!client || !redirect || !client.redirect_uris.includes(redirect)) {
    return new Response("Unknown client or redirect_uri", { status: 400 });
  }

  const back = (params: Record<string, string>) => {
    const u = new URL(redirect);
    for (const [k, v] of Object.entries(params)) u.searchParams.set(k, v);
    const s = q.get("state");
    if (s) u.searchParams.set("state", s);
    return NextResponse.redirect(u);
  };

  if (q.get("response_type") !== "code") return back({ error: "unsupported_response_type" });
  const challenge = q.get("code_challenge");
  if (!challenge || q.get("code_challenge_method") !== "S256") {
    return back({ error: "invalid_request", error_description: "PKCE (S256) is required" });
  }

  // The connector's MCP URL (the `resource` param) tells us which service, and so which provider, to sign in with.
  // If the client didn't send it (or it doesn't match), never guess: honour ?provider=, else ask the user.
  const only = serviceFromResource(q.get("resource"));
  const picked = q.get("provider");
  const provider: Provider | null = only ? SERVICE_PROVIDER[only] : isProvider(picked) ? picked : null;
  if (!provider) {
    const link = (p: Provider) => {
      const u = new URL(req.url);
      u.searchParams.set("provider", p);
      return esc(`${u.pathname}${u.search}`);
    };
    const buttons = PROVIDERS.map((p) => `<a href="${link(p)}"><button type="button">${PROVIDER_LABELS[p]}</button></a>`).join(" ");
    return page(`<h1>Sign in with…</h1><p>Which account do you want to connect?</p><p>${buttons}</p>`);
  }
  const services = only ? [only] : servicesOf(provider);

  return beginSignIn(req, provider, services, {
    client_id: clientId,
    redirect_uri: redirect,
    state: q.get("state") ?? undefined,
    code_challenge: challenge,
  });
}
