"use client";

import { usePathname, useRouter } from "next/navigation";
import { useEffect, type ReactNode } from "react";
import { Spinner } from "./ui";
import { useSession } from "@/lib/session";
import { tr } from "@/lib/i18n/tr";

/** Client-side gate for signed-in pages. The API enforces authorization independently; this only improves UX. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { me, loading } = useSession();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (!loading && !me) {
      router.replace(`/login?returnUrl=${encodeURIComponent(pathname)}`);
    }
  }, [loading, me, router, pathname]);

  if (loading || !me) {
    return (
      <div className="py-16 text-center">
        <Spinner label={tr.common.loading} />
      </div>
    );
  }
  return <>{children}</>;
}
