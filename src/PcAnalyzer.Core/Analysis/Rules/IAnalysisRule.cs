namespace PcAnalyzer.Core.Analysis.Rules;

/// <summary>
/// A diagnostic rule. Rules must only report what the recorded data supports and
/// must lower confidence when a needed metric is missing.
/// </summary>
public interface IAnalysisRule
{
    IEnumerable<Finding> Evaluate(AnalysisContext ctx);
}

internal static class RuleMath
{
    public static double CoefficientOfVariation(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count < 3) return 0;
        double mean = list.Average();
        if (mean <= 0) return 0;
        double var = list.Sum(v => (v - mean) * (v - mean)) / list.Count;
        return Math.Sqrt(var) / mean;
    }

    public static double Target(AnalysisContext ctx) =>
        ctx.RefreshHz is { } hz ? Math.Max(ctx.T.TargetFps, Math.Min(hz, 144)) : ctx.T.TargetFps;
}
