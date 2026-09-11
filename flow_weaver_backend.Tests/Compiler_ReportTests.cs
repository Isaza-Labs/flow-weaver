using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Compiler;
using flow_weaver_backend.Utils.Report.Exporters;
using Microsoft.Extensions.Logging.Abstractions;
using SnippetModel = flow_weaver_backend.Models.Snippet;
using WorkflowModel = flow_weaver_backend.Models.Workflow;

namespace flow_weaver_backend.Tests;

public class WorkflowExporterTests
{
    private static WorkflowModel Wf() => new()
    {
        WorkflowId = Guid.NewGuid(),
        Name = "reboot-flow",
        // One node + edge so the topo-sort and per-step writer loops run.
        // Exporters tolerate a missing snippet by contract, so the dict stays empty.
        Nodes = TestJson.Element("""[{"id":"n1","type":"snippet","snippet_id":"33333333-3333-3333-3333-333333333333","config":{}},{"id":"n2","type":"snippet","snippet_id":"44444444-4444-4444-4444-444444444444","config":{}}]"""),
        Edges = TestJson.Element("""[{"source":"n1","target":"n2","success":true}]"""),
    };

    private static readonly IReadOnlyDictionary<Guid, SnippetModel> NoSnippets = new Dictionary<Guid, SnippetModel>();

    [Fact]
    public async Task Python_exporter_renders_metadata_and_source()
    {
        var exp = new PythonWorkflowExporter(NullLogger<PythonWorkflowExporter>.Instance);
        Assert.Equal("python", exp.Format);
        Assert.Equal("py", exp.FileExtension);

        var src = await exp.ExportAsync(Wf(), NoSnippets, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(src));
    }

    [Fact]
    public async Task Ansible_exporter_renders_playbook()
    {
        var exp = new AnsibleWorkflowExporter(NullLogger<AnsibleWorkflowExporter>.Instance);
        Assert.Equal("ansible", exp.Format);

        var src = await exp.ExportAsync(Wf(), NoSnippets, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(src));
    }

    [Fact]
    public void Yaml_compiler_produces_document()
    {
        var compiler = new WorkflowYamlCompiler(NullLogger<WorkflowYamlCompiler>.Instance);
        var yaml = compiler.Compile(Wf());
        Assert.False(string.IsNullOrWhiteSpace(yaml));
    }
}

public class ReportExporterTests
{
    static ReportExporterTests()
    {
        // QuestPDF requires a license (Program.cs sets it at boot; tests don't run Program).
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    private static ReportDocument Doc() => new()
    {
        Title = "QA Report",
        Subtitle = "unit test",
        GeneratedBy = "tester",
        GeneratedAt = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
        Stats =
        {
            new ReportStat { Label = "Total", Value = "42", Tone = "ok" },
            new ReportStat { Label = "Failures", Value = "3", Hint = "last 24h", Tone = "critical" },
        },
        Sections =
        {
            new ReportSection
            {
                Title = "Runs",
                Description = "recent workflow runs",
                Category = "engine",
                Tables =
                {
                    new ReportTable
                    {
                        Caption = "Recent runs",
                        Headers = { "Run", "Status", "Duration" },
                        Rows =
                        {
                            new() { "run-1", "completed", "12s" },
                            new() { "run-2", "failed", "3s" },
                        },
                        SeverityColumns = new() { { 1, "critical" } },
                    },
                },
            },
        },
    };

    [Fact]
    public async Task Csv_exporter_emits_bytes()
    {
        var exp = new CsvReportExporter();
        Assert.Equal("csv", exp.Format);
        var bytes = await exp.ExportAsync(Doc(), CancellationToken.None);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task Html_exporter_emits_bytes()
    {
        var bytes = await new HtmlReportExporter().ExportAsync(Doc(), CancellationToken.None);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task Pdf_exporter_emits_pdf_magic_bytes()
    {
        var bytes = await new PdfReportExporter().ExportAsync(Doc(), CancellationToken.None);
        // "%PDF"
        Assert.True(bytes.Length > 4);
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, bytes[..4]);
    }

    [Fact]
    public async Task Excel_exporter_emits_zip_magic_bytes()
    {
        var bytes = await new ExcelReportExporter().ExportAsync(Doc(), CancellationToken.None);
        // XLSX is a zip container → "PK"
        Assert.True(bytes.Length > 2);
        Assert.Equal(new byte[] { 0x50, 0x4B }, bytes[..2]);
    }
}
