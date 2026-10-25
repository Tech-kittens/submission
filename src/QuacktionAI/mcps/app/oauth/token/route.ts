import { NextRequest } from "next/server";
import { open } from "@/lib/crypto";
import { refreshTokens, type JiraCredential } from "@/lib/jira";
import { noStore, pkceMatches, type AuthCode } from "@/lib/oauth";
import { mintToken, readRefresh, sealRefresh, type TokenPayload } from "@/lib/token";

export const runtime = "nodejs";

const fail = (error: string, description?: string) =>
  Response.json({ error, error_description: description }, { status: 400, headers: noStore });

type Refresh = Parameters<typeof sealRefresh>[0];

export async function POST(req: NextRequest) {
  const form = await req.formData().catch(() => null);
  const get = (k: string) => String(form?.get(k) ?? "");
  const grantType = get("grant_type");

  let payload: Omit<TokenPayload, "iat">;
  let refresh: Refresh | undefined;
  let expiresIn: number | undefined;

  if (grantType === "authorization_code") {
    const grant = open<AuthCode>(get("code"));
    if (!grant || grant.exp < Date.now()) return fail("invalid_grant", "Code is invalid or expired");
    if (get("client_id") && get("client_id") !== grant.client_id) return fail("invalid_grant", "client_id mismatch");
    if (get("redirect_uri") && get("redirect_uri") !== grant.redirect_uri) return fail("invalid_grant", "redirect_uri mismatch");
    if (!pkceMatches(get("code_verifier"), grant.code_challenge)) return fail("invalid_grant", "PKCE verification failed");
    payload = grant.payload;
    refresh = grant.refresh;
  } else if (grantType === "refresh_token") {
    // Jira: trade the sealed refresh token for a fresh Atlassian pair (they rotate, so we return a new one).
    const old = readRefresh(get("refresh_token"));
    if (!old) return fail("invalid_grant", "Refresh token is invalid");
    try {
      const t = await refreshTokens(old.rt);
      const cred: JiraCredential = { at: t.accessToken, exp: t.exp, cloudId: old.cloudId, site: old.site };
      payload = { provider: "jira", credential: JSON.stringify(cred), id: old.id, services: old.services };
      refresh = { rt: t.refreshToken, cloudId: old.cloudId, site: old.site, id: old.id, services: old.services };
    } catch (e) {
      return fail("invalid_grant", e instanceof Error ? e.message : "Refresh failed");
    }
  } else {
    return fail("unsupported_grant_type");
  }

  if (payload.provider === "jira") {
    const cred = JSON.parse(payload.credential) as JiraCredential;
    expiresIn = Math.max(60, Math.floor((cred.exp - Date.now()) / 1000));
  }

  return Response.json(
    {
      access_token: mintToken(payload),
      token_type: "Bearer",
      ...(expiresIn ? { expires_in: expiresIn } : {}),
      ...(refresh ? { refresh_token: sealRefresh(refresh) } : {}),
    },
    { headers: noStore },
  );
}
