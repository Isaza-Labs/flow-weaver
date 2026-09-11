using System.Net;
using System.Text;
using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Utils.Report.Exporters;

// Renders a ReportDocument as a single self-contained HTML file that
// mirrors the visual language of reporte.html: hero header, stat-grid,
// section blocks, tables with sev badges, callouts. CSS lives inline so
// the file opens from disk / email attachment / base64 without needing
// a CDN. Fonts fall back to system stack if Google Fonts is blocked.
public sealed class HtmlReportExporter : IReportExporter
{
    public string Format => "html";
    public string ContentType => "text/html; charset=utf-8";
    public string FileExtension => "html";

    public Task<byte[]> ExportAsync(ReportDocument doc, CancellationToken ct)
    {
        var sb = new StringBuilder(16 * 1024);

        sb.Append("<!DOCTYPE html>\n<html lang=\"es\"><head>\n");
        sb.Append("<meta charset=\"UTF-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n");
        sb.Append($"<title>{Escape(doc.Title)}</title>\n");
        sb.Append("<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\">\n");
        sb.Append("<link href=\"https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700;800&family=JetBrains+Mono:wght@400;500;600&display=swap\" rel=\"stylesheet\">\n");
        sb.Append("<style>\n").Append(Css).Append("</style>\n");
        sb.Append("</head><body>\n");

        sb.Append("<div class=\"container\">\n");

        // Hero
        sb.Append("<div class=\"hero\">\n");
        if (!string.IsNullOrWhiteSpace(doc.Badge))
            sb.Append($"<div class=\"badge\">{Escape(doc.Badge!)}</div>\n");
        sb.Append($"<h1>{Escape(doc.Title)}</h1>\n");
        if (!string.IsNullOrWhiteSpace(doc.Subtitle))
            sb.Append($"<p>{Escape(doc.Subtitle!)}</p>\n");
        var gen = doc.GeneratedAt ?? DateTime.UtcNow;
        sb.Append($"<p class=\"meta\">Generated {Escape(gen.ToString("yyyy-MM-dd HH:mm 'UTC'"))}");
        if (!string.IsNullOrWhiteSpace(doc.GeneratedBy))
            sb.Append($" · {Escape(doc.GeneratedBy!)}");
        sb.Append("</p>\n");
        sb.Append("</div>\n");

        // Stats
        if (doc.Stats.Count > 0)
        {
            sb.Append("<div class=\"stats-grid\">\n");
            foreach (var s in doc.Stats)
            {
                sb.Append($"<div class=\"stat-card stat-{ToneSlug(s.Tone)}\">\n");
                sb.Append($"  <div class=\"stat-label\">{Escape(s.Label)}</div>\n");
                sb.Append($"  <div class=\"stat-value\">{Escape(s.Value)}</div>\n");
                if (!string.IsNullOrWhiteSpace(s.Hint))
                    sb.Append($"  <div class=\"stat-hint\">{Escape(s.Hint!)}</div>\n");
                sb.Append("</div>\n");
            }
            sb.Append("</div>\n");
        }

        // Sections
        foreach (var section in doc.Sections)
        {
            var cat = ToneSlug(section.Category ?? "accent");
            sb.Append("<section class=\"category-section\">\n");
            sb.Append($"<h2 class=\"section-title\">{Escape(section.Title)}</h2>\n");
            if (!string.IsNullOrWhiteSpace(section.Description))
                sb.Append($"<p class=\"section-sub\">{Escape(section.Description!)}</p>\n");

            foreach (var table in section.Tables)
            {
                sb.Append($"<div class=\"cat-{cat}\">\n");
                if (!string.IsNullOrWhiteSpace(table.Caption))
                    sb.Append($"<div class=\"category-header\">{Escape(table.Caption!)}</div>\n");
                sb.Append("<table>\n<thead><tr>");
                foreach (var h in table.Headers)
                    sb.Append($"<th>{Escape(h)}</th>");
                sb.Append("</tr></thead>\n<tbody>\n");
                foreach (var row in table.Rows)
                {
                    sb.Append("<tr>");
                    for (var i = 0; i < row.Count; i++)
                    {
                        var cell = row[i];
                        if (table.SeverityColumns is not null
                            && table.SeverityColumns.TryGetValue(i, out var sev))
                        {
                            sb.Append($"<td><span class=\"sev-badge sev-{ToneSlug(sev)}\">{Escape(cell)}</span></td>");
                        }
                        else
                        {
                            sb.Append($"<td>{Escape(cell)}</td>");
                        }
                    }
                    sb.Append("</tr>\n");
                }
                sb.Append("</tbody></table>\n</div>\n");
            }

            foreach (var callout in section.Callouts)
            {
                sb.Append($"<div class=\"callout {CalloutToneClass(callout.Tone)}\">\n");
                sb.Append($"  <div class=\"callout-title\">{Escape(callout.Title)}</div>\n");
                sb.Append($"  <p>{Escape(callout.Body)}</p>\n");
                sb.Append("</div>\n");
            }

            sb.Append("</section>\n");
        }

        sb.Append("<div class=\"footer\">Generated by FlowWeaver</div>\n");
        sb.Append("</div></body></html>\n");

        return Task.FromResult(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static string Escape(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

    // Normalize tone so unknown values still render without broken CSS.
    private static string ToneSlug(string? tone) => (tone ?? "neutral").ToLowerInvariant() switch
    {
        "critical" or "high" or "medium" or "low" or "ok" or "accent" => tone!.ToLowerInvariant(),
        "success" => "ok",
        "warning" or "warn" => "medium",
        "danger" or "error" => "high",
        "info" => "low",
        _ => "neutral",
    };

    private static string CalloutToneClass(string? tone) => (tone ?? "info").ToLowerInvariant() switch
    {
        "warn" or "warning" => "warn",
        "danger" or "error" => "danger",
        "ok" or "success" => "success",
        _ => string.Empty,
    };

    // CSS is a trimmed copy of reporte.html's `:root` + hero + stat-card
    // + section-title + table + sev-badge + callout + footer styles.
    // We keep *only* what an exported report actually uses — no top-bar,
    // no mermaid, no top nav, no progress cards.
    private const string Css = """
    :root {
      --bg:#f8f9fc; --surface:#ffffff; --surface-alt:#f1f3f9;
      --border:#e2e5ef; --border-focus:#6366f1;
      --text:#1e1e2e; --text-muted:#64668b;
      --accent:#6366f1; --accent-light:#eef2ff; --accent-dark:#4f46e5;
      --green:#10b981; --green-bg:#ecfdf5; --green-border:#a7f3d0;
      --orange:#f59e0b; --orange-bg:#fffbeb; --orange-border:#fde68a;
      --red:#ef4444; --red-bg:#fef2f2; --red-border:#fecaca;
      --red-dark:#b91c1c; --red-darkbg:#fff1f2;
      --blue:#3b82f6; --blue-bg:#eff6ff; --blue-border:#bfdbfe;
      --purple:#8b5cf6; --purple-bg:#f5f3ff; --purple-border:#ddd6fe;
      --teal:#14b8a6; --teal-bg:#f0fdfa; --teal-border:#99f6e4;
      --cyan:#06b6d4; --cyan-bg:#ecfeff; --cyan-border:#a5f3fc;
      --radius:12px; --radius-sm:8px;
    }
    *{margin:0;padding:0;box-sizing:border-box;}
    body{font-family:'Inter',-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;background:var(--bg);color:var(--text);line-height:1.7;-webkit-font-smoothing:antialiased;padding-bottom:40px;}
    .container{max-width:1280px;margin:0 auto;padding:0 2rem;}
    .hero{padding:2.5rem 0 1.75rem;text-align:center;}
    .hero .badge{display:inline-flex;align-items:center;gap:6px;font-size:.75rem;font-weight:600;text-transform:uppercase;letter-spacing:.06em;color:var(--accent);background:var(--accent-light);border:1px solid var(--border-focus);border-radius:100px;padding:5px 14px;margin-bottom:1rem;}
    .hero h1{font-size:2.4rem;font-weight:800;letter-spacing:-0.03em;line-height:1.1;margin-bottom:.75rem;background:linear-gradient(135deg,var(--text) 0%,var(--accent-dark) 100%);-webkit-background-clip:text;-webkit-text-fill-color:transparent;background-clip:text;}
    .hero p{font-size:1rem;color:var(--text-muted);max-width:780px;margin:0 auto;}
    .hero p.meta{font-size:.78rem;margin-top:.5rem;opacity:.7;}
    .stats-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:14px;margin-bottom:28px;}
    .stat-card{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:18px;box-shadow:0 1px 2px rgba(0,0,0,0.04);}
    .stat-label{color:var(--text-muted);font-size:.72rem;text-transform:uppercase;letter-spacing:.06em;margin-bottom:6px;font-weight:600;}
    .stat-value{font-size:1.9rem;font-weight:800;line-height:1;letter-spacing:-0.02em;}
    .stat-hint{font-size:.78rem;color:var(--text-muted);margin-top:6px;}
    .stat-critical .stat-value{color:var(--red-dark);}
    .stat-high .stat-value{color:var(--red);}
    .stat-medium .stat-value{color:var(--orange);}
    .stat-low .stat-value{color:var(--blue);}
    .stat-ok .stat-value{color:var(--green);}
    .stat-accent .stat-value{color:var(--accent);}
    .section-title{font-size:1.4rem;font-weight:800;margin:32px 0 6px;letter-spacing:-0.02em;}
    .section-sub{color:var(--text-muted);margin-bottom:20px;font-size:.92rem;}
    .category-section{margin-bottom:26px;}
    .category-header{padding:10px 16px;border-radius:var(--radius-sm) var(--radius-sm) 0 0;font-weight:700;font-size:.95rem;letter-spacing:.3px;background:var(--accent-light);color:var(--accent);border:1px solid var(--border-focus);border-bottom:none;}
    .cat-critical .category-header{background:var(--red-darkbg);color:var(--red-dark);border-color:var(--red-border);}
    .cat-api .category-header{background:var(--blue-bg);color:var(--blue);border-color:var(--blue-border);}
    .cat-ai .category-header{background:var(--purple-bg);color:var(--purple);border-color:var(--purple-border);}
    .cat-engine .category-header{background:var(--green-bg);color:var(--green);border-color:var(--green-border);}
    .cat-store .category-header{background:var(--teal-bg);color:var(--teal);border-color:var(--teal-border);}
    .cat-worker .category-header{background:var(--orange-bg);color:var(--orange);border-color:var(--orange-border);}
    .cat-scheduler .category-header{background:var(--cyan-bg);color:var(--cyan);border-color:var(--cyan-border);}
    .cat-accent .category-header{background:var(--accent-light);color:var(--accent);border-color:var(--border-focus);}
    table{width:100%;border-collapse:collapse;background:var(--surface);border:1px solid var(--border);border-radius:0 0 var(--radius-sm) var(--radius-sm);overflow:hidden;font-size:.86rem;margin-bottom:14px;}
    th{background:var(--surface-alt);text-align:left;padding:10px 14px;font-size:.72rem;text-transform:uppercase;letter-spacing:.06em;color:var(--text-muted);border-bottom:2px solid var(--border);font-weight:600;}
    td{padding:10px 14px;border-bottom:1px solid var(--border);font-size:.88rem;vertical-align:top;}
    tr:last-child td{border-bottom:none;}
    tr:hover{background:var(--surface-alt);}
    .sev-badge{display:inline-block;font-size:.68rem;padding:2px 8px;border-radius:10px;font-weight:700;text-transform:uppercase;letter-spacing:.04em;border:1px solid;}
    .sev-critical{background:var(--red-darkbg);color:var(--red-dark);border-color:var(--red-border);}
    .sev-high{background:var(--red-bg);color:var(--red);border-color:var(--red-border);}
    .sev-medium{background:var(--orange-bg);color:var(--orange);border-color:var(--orange-border);}
    .sev-low{background:var(--blue-bg);color:var(--blue);border-color:var(--blue-border);}
    .sev-ok{background:var(--green-bg);color:var(--green);border-color:var(--green-border);}
    .sev-neutral{background:var(--surface-alt);color:var(--text-muted);border-color:var(--border);}
    .sev-accent{background:var(--accent-light);color:var(--accent);border-color:var(--border-focus);}
    .callout{background:var(--surface);border-left:4px solid var(--accent);border-radius:var(--radius-sm);padding:14px 18px;margin:14px 0;box-shadow:0 1px 2px rgba(0,0,0,0.04);}
    .callout.warn{border-left-color:var(--orange);background:var(--orange-bg);}
    .callout.danger{border-left-color:var(--red);background:var(--red-bg);}
    .callout.success{border-left-color:var(--green);background:var(--green-bg);}
    .callout-title{font-weight:700;margin-bottom:4px;font-size:.9rem;}
    .callout p{font-size:.9rem;color:var(--text);}
    .footer{margin-top:40px;padding:20px 0;border-top:1px solid var(--border);color:var(--text-muted);font-size:.78rem;text-align:center;}
    """;
}
