export type WebhookTargetStatus = "Pending" | "Verified" | "VerificationFailed" | "Revoked";

export interface WebhookTarget {
  id: string;
  url: string;
  adapterPath: string | null;
  status: WebhookTargetStatus;
  secretHint: string;
  verifiedAt: string | null;
  verificationExpiresAt: string | null;
  lastVerificationAttemptAt: string | null;
  lastVerificationError: string | null;
  createdAt: string;
  isDevLocal: boolean;
}

export interface CreatedWebhookTarget {
  target: WebhookTarget;
  signingSecret: string;
}

export interface Delivery {
  deliveryId: string;
  eventId: string;
  aggregateId: string;
  eventVersion: number;
  eventType: string;
  plannedOrder: number;
  attemptNumber: number;
  scheduledAt: string;
  sentAt: string | null;
  completedAt: string | null;
  responseStatus: number | null;
  durationMs: number | null;
  outcome: "Pending" | "Delivered" | "Failed" | "TimedOut" | "Blocked" | "Cancelled" | "Unknown";
  error: string | null;
}

export const WEBHOOK_ENGINES = ["duplicate-webhook", "out-of-order-webhook"];

export const verificationErrors: Record<string, string> = {
  challenge_mismatch: "Alıcı challenge'a doğru HMAC yanıtı vermedi (imza sırrı farklı olabilir).",
  challenge_invalid_response: "Alıcı yanıtı beklenen JSON biçiminde değil.",
  blocked_address: "Adres özel/iç ağa çözümlendiği için engellendi.",
  dns_failed: "Alan adı çözümlenemedi.",
  timeout: "Alıcı zamanında yanıt vermedi.",
  tls_failed: "TLS sertifikası doğrulanamadı.",
  redirect_not_followed: "Alıcı yönlendirme döndürdü; yönlendirmeler izlenmez.",
  response_too_large: "Yanıt boyut sınırını aştı.",
  connect_failed: "Bağlantı kurulamadı.",
};
