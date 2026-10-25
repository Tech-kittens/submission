import { env } from "@/lib/env";
import type { Provider } from "@/lib/scopes";

export function appUrl(req: Request): string {
  return (process.env.APP_URL ?? new URL(req.url).origin).replace(/\/$/, "") || env("APP_URL");
}

/** Where Google / GitHub send the user back after sign-in. Register these in each provider's console. */
export const redirectUri = (req: Request, provider: Provider) => `${appUrl(req)}/api/auth/${provider}/callback`;
