"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { ConfirmDialog } from "./confirm-dialog";
import { Alert, Button, Card } from "./ui";
import { api, ApiError, resetCsrf } from "@/lib/api";
import { useSession } from "@/lib/session";
import { tr } from "@/lib/i18n/tr";

export function AccountDeletion() {
  const router = useRouter();
  const { setMe } = useSession();
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);

  return (
    <Card className="border-bad/40">
      <h2 className="mb-1 font-semibold">{tr.account.deleteAccount}</h2>
      <p className="mb-3 text-sm text-muted">
        Tüm projeler, koşular, raporlar, özel senaryolar ve paylaşım bağlantıları silinir; aktif koşular iptal edilir. Bu işlem geri alınamaz.
      </p>
      {error && <Alert tone="error">{error}</Alert>}
      <ConfirmDialog
        title={tr.account.deleteAccount}
        confirmLabel={tr.account.deleteAccount}
        danger
        requirePhrase="HESABIMI SIL"
        onConfirm={async () => {
          setError(null);
          try {
            await api("/api/account", { method: "DELETE", body: { password } });
            setPassword("");
            resetCsrf();
            setMe(null);
            router.push("/");
          } catch (e) {
            setError(e instanceof ApiError ? e.message : tr.common.errorTitle);
          }
        }}
        trigger={(open) => (
          <Button variant="danger" onClick={open}>
            {tr.account.deleteAccount}
          </Button>
        )}
      >
        <label htmlFor="delete-password" className="mb-1 block">
          Parolanız
        </label>
        <input
          id="delete-password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          className="mb-3 min-h-10 w-full rounded-md border border-ink-500 bg-ink-900 px-3"
        />
        <p>Onaylamak için aşağıya yazın:</p>
      </ConfirmDialog>
    </Card>
  );
}
