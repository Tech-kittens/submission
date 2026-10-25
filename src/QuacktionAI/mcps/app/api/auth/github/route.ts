import { beginSignIn } from "@/lib/signin";
import { servicesOf } from "@/lib/scopes";

export const runtime = "nodejs";

export async function GET(req: Request) {
  return beginSignIn(req, "github", servicesOf("github"));
}
