import { NextRequest } from "next/server";
import { appUrl } from "@/lib/app-url";

export const runtime = "nodejs";

export async function GET(req: NextRequest) {
  const base = appUrl(req);
  return Response.json({
    issuer: base,
    authorization_endpoint: `${base}/oauth/authorize`,
    token_endpoint: `${base}/oauth/token`,
    registration_endpoint: `${base}/oauth/register`,
    response_types_supported: ["code"],
    grant_types_supported: ["authorization_code", "refresh_token"],
    code_challenge_methods_supported: ["S256"],
    token_endpoint_auth_methods_supported: ["none"],
  });
}
