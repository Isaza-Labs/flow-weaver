using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Utils.Report;

// Contract for a single output format. Each implementation owns exactly
// one Format string + its MIME + file extension and knows how to render
// a neutral ReportDocument into bytes.
//
// Registered as IReportExporter (multiple); ReportService picks one by
// matching Format (case-insensitive). Exporters are stateless so
// singleton DI is fine.
public interface IReportExporter
{
    /// <summary>Lowercase — "html" | "csv" | "xlsx" | "pdf".</summary>
    string Format { get; }

    /// <summary>Standard MIME sent in Content-Type headers.</summary>
    string ContentType { get; }

    /// <summary>Lowercase, no leading dot.</summary>
    string FileExtension { get; }

    /// <summary>Produces the final byte payload. Never throws for empty
    /// inputs — an empty document renders an empty-but-valid file.</summary>
    Task<byte[]> ExportAsync(ReportDocument doc, CancellationToken ct);
}
