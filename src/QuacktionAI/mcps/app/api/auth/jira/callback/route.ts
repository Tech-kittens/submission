import { NextRequest } from "next/server";
import { handleCallback } from "@/lib/signin";

export const runtime = "nodejs";

export const GET = (req: NextRequest) => handleCallback(req, "jira");
