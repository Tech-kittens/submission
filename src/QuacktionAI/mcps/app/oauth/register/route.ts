import { NextRequest } from "next/server";
import { seal } from "@/lib/crypto";
import { noStore, redirectAllowed, type ClientInfo } from "@/lib/oauth";

export const runtime = "nodejs";

const fail = (error: string, description: string) =>
  Response.json({ error, error_description: description }, { status: 400, headers: noStore });

/** Stateless dynamic client registration: the client_id *is* the sealed registration. */
export async function POST(req: NextRequest) {
  const body = (await req.json().catch(() => null)) as { redirect_uris?: unknown; client_name?: unknown } | null;
  const uris = body?.redirect_uris;
  if (!Array.isArray(uris) || !uris.length || !uris.every((u) => typeof u === "string")) {
    return fail("invalid_redirect_uri", "redirect_uris must be a non-empty array of strings");
  }
  if (!uris.every(redirectAllowed)) return fail("invalid_redirect_uri", "redirect_uri not allowed");

  const info: ClientInfo = {
    redirect_uris: uris,
    client_name: typeof body?.client_name === "string" ? body.client_name : undefined,
  };
  return Response.json(
    {
      client_id: seal(info),
      client_name: info.client_name,
      redirect_uris: uris,
      grant_types: ["authorization_code", "refresh_token"],
      response_types: ["code"],
      token_endpoint_auth_method: "none",
    },
    { status: 201, headers: noStore },
  );
}
