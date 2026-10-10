using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using APIArena.Application.Abstractions;
using APIArena.Application.Access;
using APIArena.Application.Common;
using APIArena.Domain.Entities;
using APIArena.Domain.Runs;
using APIArena.Domain.Scenarios;
using Microsoft.EntityFrameworkCore;

namespace APIArena.Application.Reports;

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

public sealed class ReportService(IAppDbContext db, AccessScope scope, TimeProvider clock)
{
    public const int MaxExportBytes = 2 * 1024 * 1024;

    public async Task<ReportDocument> GetAsync(Guid userId, Guid runId, CancellationToken ct)
    {
        var run = await scope.RequireRunAsync(userId, runId, ct);
        var json = await db.ReportSnapshots.AsNoTracking().Where(s => s.RunId == runId).Select(s => s.ReportJson).FirstOrDefaultAsync(ct);
        if (json is null)
        {
            throw RunStateMachine.IsTerminal(run.Status)
                ? new ConflictException("report_pending", "Rapor hazırlanıyor. Birkaç saniye sonra tekrar deneyin.")
                : new ConflictException("run_not_finished", "Rapor, koşu bittiğinde (bitir/iptal/süre dolumu) oluşturulur.");
        }

        return JsonSerializer.Deserialize<ReportDocument>(json, ScenarioJson.Options)!;
    }

    public async Task<CompareResult> CompareAsync(Guid userId, Guid left, Guid right, CancellationToken ct)
    {
        if (left == right)
        {
            throw ValidationFailedException.For("right", "Karşılaştırma için iki farklı koşu seçin.");
        }

        var a = await GetAsync(userId, left, ct);
        var b = await GetAsync(userId, right, ct);
        var warnings = new List<string>();
        var comparable = a.ScenarioSlug == b.ScenarioSlug;
        if (!comparable)
        {
            warnings.Add($"Farklı senaryolar ({a.ScenarioSlug} ↔ {b.ScenarioSlug}): kontroller birebir karşılaştırılamaz; yalnızca genel sayılar gösterilir.");
        }
        else if (a.ScenarioVersion != b.ScenarioVersion)
        {
            warnings.Add($"Senaryo sürümleri farklı (v{a.ScenarioVersion} ↔ v{b.ScenarioVersion}): davranış veya eşikler değişmiş olabilir.");
        }

        if (a.EvaluatorVersion != b.EvaluatorVersion)
        {
            warnings.Add($"Değerlendirici sürümleri farklı ({a.EvaluatorVersion} ↔ {b.EvaluatorVersion}): aynı trafik farklı sonuç verebilir.");
        }

        foreach (var (side, r) in new[] { ("Sol", a), ("Sağ", b) })
        {
            if (r.IncompleteReason is not null)
            {
                warnings.Add($"{side} koşu eksik: {r.IncompleteReasonText}. Inconclusive sonuçlar başarı değildir.");
            }
        }

        var rows = new List<CompareCheckRow>();
        if (comparable)
        {
            var ids = a.Checks.Select(c => c.Id).Union(b.Checks.Select(c => c.Id));
            foreach (var id in ids)
            {
                var l = a.Checks.FirstOrDefault(c => c.Id == id);
                var r = b.Checks.FirstOrDefault(c => c.Id == id);
                rows.Add(new CompareCheckRow(id, (r ?? l)!.Title, l?.Status, r?.Status, Change(l?.Status, r?.Status)));
            }
        }

        return new CompareResult(Side(a), Side(b), comparable, warnings, rows);
    }

    private static CompareSide Side(ReportDocument r) =>
        new(r.RunId, r.ScenarioSlug, r.ScenarioVersion, r.EvaluatorVersion, r.RunStatus, r.Label, r.CreatedAt, r.Counts, r.RequestCount, r.RunDurationMs, r.IncompleteReason);

    private static string Change(CheckStatus? left, CheckStatus? right) => (left, right) switch
    {
        (null, _) => "new",
        (_, null) => "removed",
        var (l, r) when l == r => "same",
        (_, CheckStatus.Passed) => "improved",
        (CheckStatus.Passed, _) => "regressed",
        (CheckStatus.Failed, CheckStatus.Inconclusive) => "less-evidence",
        (CheckStatus.Inconclusive, CheckStatus.Failed) => "now-failed",
        _ => "changed",
    };

    public async Task<ExportFile> ExportAsync(Guid userId, Guid runId, string format, CancellationToken ct)
    {
        var report = await GetAsync(userId, runId, ct);
        // Server-generated name only: no user input reaches the file name (no traversal / header injection).
        var name = $"apiarena-report-{report.RunId.ToString("N")[..8]}-{report.GeneratedAt:yyyyMMdd}";
        var file = format switch
        {
            "json" => new ExportFile(name + ".json", "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(new { exportedAt = clock.GetUtcNow(), masked = true, report }, new JsonSerializerOptions(ScenarioJson.Options) { WriteIndented = true })),
            "html" => new ExportFile(name + ".html", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(RenderHtml(report, clock.GetUtcNow()))),
            _ => throw ValidationFailedException.For("format", "format 'json' veya 'html' olmalı."),
        };
        if (file.Content.Length > MaxExportBytes)
        {
            throw new ConflictException("export_too_large", "Rapor dışa aktarma sınırını aşıyor.");
        }

        return file;
    }

    /// <summary>
    /// Standalone, script-free HTML. Every dynamic value is HTML-encoded; a restrictive CSP meta tag blocks scripts,
    /// external loads and framing even if the file is opened from disk.
    /// </summary>
    public static string RenderHtml(ReportDocument r, DateTimeOffset exportedAt)
    {
        static string E(string? s) => HtmlEncoder.Default.Encode(s ?? "");
        string StatusLabel(CheckStatus s) => s switch
        {
            CheckStatus.Passed => "✓ Passed",
            CheckStatus.Failed => "✕ Failed",
            CheckStatus.Inconclusive => "? Inconclusive",
            _ => "– NotApplicable",
        };

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"tr\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src 'none'; base-uri 'none'; form-action 'none'\">");
        sb.Append("<meta name=\"referrer\" content=\"no-referrer\"><meta name=\"robots\" content=\"noindex\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>API Arena raporu – ").Append(E(r.ScenarioTitle)).Append("</title><style>");
        sb.Append("body{font-family:system-ui,Segoe UI,Arial,sans-serif;background:#ffffff;color:#0f172a;margin:0;padding:24px;line-height:1.5;border-top:6px solid #2563eb}h1{border-bottom:4px solid #facc15;padding-bottom:4px}");
        sb.Append("main{max-width:960px;margin:auto}table{border-collapse:collapse;width:100%;font-size:14px}th,td{border:1px solid #e8dfc4;padding:6px 8px;text-align:left;vertical-align:top;background:#fff}");
        sb.Append("code,.mono{font-family:Consolas,Menlo,monospace}.s-Passed{color:#15803d}.s-Failed{color:#b91c1c}.s-Inconclusive{color:#a16207}.s-NotApplicable{color:#57534e}");
        sb.Append(".box{border:1px solid #e8dfc4;background:#fff;border-radius:8px;padding:12px;margin:12px 0}.warn{border-color:#a16207}</style></head><body><main>");
        sb.Append("<h1>").Append(E(r.ScenarioTitle)).Append("</h1>");
        sb.Append("<p class=\"mono\">").Append(E(r.ScenarioSlug)).Append(" v").Append(r.ScenarioVersion).Append(" · değerlendirici ").Append(E(r.EvaluatorVersion))
            .Append(" · koşu ").Append(E(r.RunId.ToString())).Append("</p>");
        sb.Append("<p>Proje: ").Append(E(r.ProjectName)).Append(" · Durum: <strong>").Append(E(r.RunStatus.ToString())).Append("</strong>");
        if (r.Label is not null)
        {
            sb.Append(" · Etiket: ").Append(E(r.Label));
        }

        sb.Append("<br>Rapor üretimi: ").Append(E(r.GeneratedAt.ToString("u", CultureInfo.InvariantCulture))).Append(" · Dışa aktarma: ")
            .Append(E(exportedAt.ToString("u", CultureInfo.InvariantCulture))).Append(" (UTC)</p>");
        if (r.IncompleteReasonText is not null)
        {
            sb.Append("<div class=\"box warn\"><strong>Eksik koşu:</strong> ").Append(E(r.IncompleteReasonText)).Append(". Eksik kanıta dayalı sonuçlar Inconclusive sayıldı.</div>");
        }

        sb.Append("<div class=\"box\"><strong>Sonuçlar:</strong> ").Append(r.Counts.Passed).Append(" passed · ").Append(r.Counts.Failed).Append(" failed · ")
            .Append(r.Counts.Inconclusive).Append(" inconclusive · ").Append(r.Counts.NotApplicable).Append(" not applicable. ")
            .Append("Değerlendirilebilir kontrol: ").Append(r.Counts.Applicable).Append(". İstek: ").Append(r.RequestCount)
            .Append(", reddedilen: ").Append(r.RejectedCount).Append("<br><em>").Append(E(r.Notice)).Append("</em></div>");

        sb.Append("<h2>Kontroller</h2><table><thead><tr><th>Kontrol</th><th>Sonuç</th><th>Beklenen</th><th>Gözlenen</th><th>Açıklama / öneri</th></tr></thead><tbody>");
        foreach (var c in r.Checks)
        {
            sb.Append("<tr><td>").Append(E(c.Title)).Append("<br><code>").Append(E(c.Id)).Append("</code></td><td class=\"s-").Append(c.Status).Append("\">")
                .Append(E(StatusLabel(c.Status))).Append("<br><small>").Append(E(c.EvidenceKind.ToString())).Append("</small></td><td>").Append(E(c.Expected))
                .Append("</td><td>").Append(E(c.Observed)).Append("<br><small>Kanıt: ").Append(E(string.Join(", ", c.Evidence.Take(20).Select(x => "#" + x))))
                .Append("</small></td><td>").Append(E(c.Explanation));
            if (c.Suggestion is not null)
            {
                sb.Append("<br><strong>Öneri:</strong> ").Append(E(c.Suggestion));
            }

            sb.Append("</td></tr>");
        }

        sb.Append("</tbody></table><h2>Neyi doğrulayamadık?</h2><ul>");
        foreach (var u in r.Unverifiable)
        {
            sb.Append("<li>").Append(E(u)).Append("</li>");
        }

        sb.Append("</ul><h2>Zaman çizelgesi</h2><table><thead><tr><th>#</th><th>+ms</th><th>İstek</th><th>Deneme</th><th>Planlanan</th><th>Sonuç</th><th>Süre</th></tr></thead><tbody>");
        foreach (var t in r.Timeline)
        {
            sb.Append("<tr><td>").Append(t.Sequence).Append("</td><td>").Append(t.OffsetMs).Append("</td><td class=\"mono\">").Append(E(t.Method)).Append(" /").Append(E(t.Route))
                .Append(t.OperationKey is null ? "" : "<br><small>key: " + E(t.OperationKey) + "</small>").Append("</td><td>").Append(t.Attempt).Append("</td><td>")
                .Append(t.PlannedStatus).Append("</td><td>").Append(E(t.Outcome.ToString())).Append(t.RejectReason is null ? "" : " (" + E(t.RejectReason) + ")")
                .Append("</td><td>").Append(t.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "-").Append("</td></tr>");
        }

        sb.Append("</tbody></table>");
        if (r.TimelineTruncated)
        {
            sb.Append("<p>Zaman çizelgesi kısaltıldı.</p>");
        }

        sb.Append("<p><small>Maskelenmiş dışa aktarma: kimlik bilgileri, çerezler ve gövde içerikleri dahil edilmez.</small></p></main></body></html>");
        return sb.ToString();
    }
}
