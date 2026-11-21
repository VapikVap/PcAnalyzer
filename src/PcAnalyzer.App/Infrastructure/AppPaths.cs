namespace PcAnalyzer.App.Infrastructure;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcAnalyzer");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string Database => Path.Combine(DataDir, "pcanalyzer.db");
    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string DefaultExportDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PC Performance Analyzer");
    public static string ExePath => Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
}
