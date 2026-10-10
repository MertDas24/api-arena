import { cpSync, existsSync, mkdirSync } from "node:fs";

// Next's standalone server needs these directories copied separately.
mkdirSync(".next/standalone/.next", { recursive: true });
cpSync(".next/static", ".next/standalone/.next/static", { recursive: true });
if (existsSync("public")) cpSync("public", ".next/standalone/public", { recursive: true });
