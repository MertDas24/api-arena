"use client";

import Link from "next/link";
import { tr } from "@/lib/i18n/tr";

/** Only rendered in local development builds: points to the file-based mail sink instead of a real inbox. */
export function DevMailboxHint({ email }: { email?: string }) {
  if (process.env.NODE_ENV !== "development") return null;
  return (
    <p className="mt-3 rounded-md border border-dashed border-warn/60 p-2 text-xs text-warn">
      {tr.auth.devMailboxNote}{" "}
      <Link className="underline" href={`/dev/mailbox${email ? `?to=${encodeURIComponent(email)}` : ""}`}>
        {tr.auth.devMailbox}
      </Link>
    </p>
  );
}
