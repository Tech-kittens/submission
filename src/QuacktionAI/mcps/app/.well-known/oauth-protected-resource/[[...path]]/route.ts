import { NextRequest } from "next/server";
import { appUrl } from "@/lib/app-url";

export const runtime = "nodejs";

export async function GET(req: NextRequest, ctx: { params: Promise<{ path?: string[] }> }) {
  const { path } = await ctx.params;
  const base = appUrl(req);
  return Response.json({
    resource: path?.length ? `${base}/${path.join("/")}` : base,
    authorization_servers: [base],
    bearer_methods_supported: ["header"],
  });
}
