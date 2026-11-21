using PcAnalyzer.Core.Analysis;
using PcAnalyzer.Core.Export;

namespace PcAnalyzer.Core.Tests;

public class ExportTests
{
    [Fact]
    public void HtmlAndCsv_AreGeneratedWithoutInventingMissingValues()
    {
        var b = new SessionBuilder();
        b.Add(120, (_, s) => { s.GpuLoad = 98; s.CpuTempC = null; }, _ => b.Steady(55));
        var input = b.Build("Игра <test>");
        var (summary, report) = new PerformanceAnalyzer().Analyze(input);
        input.Session.Summary = summary;
        input.Session.Report = report;

        var html = HtmlReportExporter.Render(input.Session, input.Samples);
        Assert.Contains("Игра &lt;test&gt;", html);          // encoded
        Assert.Contains("<svg", html);                        // charts embedded
        Assert.Contains(report.Headline, html);

        var path = Path.Combine(Path.GetTempPath(), $"pca-{Guid.NewGuid():N}.csv");
        try
        {
            CsvExporter.WriteTelemetry(path, input.Session, input.Samples);
            var lines = File.ReadAllLines(path);
            Assert.Equal(122, lines.Length);                  // comment + header + 120 rows
            var header = lines[1].Split(',');
            int tempCol = Array.IndexOf(header, "cpu_temp_c");
            Assert.Equal("", lines[2].Split(',')[tempCol]);   // unavailable → empty, not 0
        }
        finally { File.Delete(path); }
    }
}
