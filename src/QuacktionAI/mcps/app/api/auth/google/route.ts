import { NextRequest } from "next/server";
import { beginSignIn } from "@/lib/signin";
import { SERVICE_PROVIDER, isService, servicesOf } from "@/lib/scopes";

export const runtime = "nodejs";

export async function GET(req: NextRequest) {
  const requested = req.nextUrl.searchParams
    .getAll("service")
    .filter(isService)
    .filter((s) => SERVICE_PROVIDER[s] === "google");
  const services = requested.length ? [...new Set(requested)] : servicesOf("google");
  return beginSignIn(req, "google", services);
}
