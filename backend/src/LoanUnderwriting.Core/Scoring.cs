namespace LoanUnderwriting.Core;

public static class EbitdaCalculator
{
    /// <summary>EBITDA = taxable income + interest + depreciation + officer compensation (benchmark formula).</summary>
    public static DerivedMetrics Derive(ExtractedMetrics m)
    {
        var ebitda = m.TaxableIncome + m.Interest + m.Depreciation + m.OfficerComp;
        var debt = m.ShortTermDebt + m.LongTermDebt;
        // A non-positive EBITDA cannot service debt; use a large sentinel so it scores at the floor.
        var leverage = ebitda > 0 ? debt / ebitda : 99m;
        var margin = m.NetRevenue > 0 ? ebitda / m.NetRevenue : 0m;
        var cover = m.Interest > 0 ? ebitda / m.Interest : 99m;
        return new DerivedMetrics(ebitda, ebitda - m.OfficerComp, debt, leverage, margin, cover);
    }

    /// <summary>Gross - returns = net revenue (0.5% tolerance), and stated EBITDA (if any) matches recomputed (1%).</summary>
    public static bool Reconciles(ExtractedMetrics m)
    {
        var netOk = Within(m.GrossReceipts - m.Returns, m.NetRevenue, 0.005m);
        var ebitda = Derive(m).Ebitda;
        var ebitdaOk = m.StatedEbitda is null || Within(ebitda, m.StatedEbitda.Value, 0.01m);
        return netOk && ebitdaOk;
    }

    private static bool Within(decimal a, decimal b, decimal tol) =>
        b == 0 ? a == 0 : Math.Abs(a - b) / Math.Abs(b) <= tol;
}

public static class ScorecardService
{
    private static decimal Lerp(decimal x, decimal x0, decimal x1, decimal y0, decimal y1) => y0 + (x - x0) / (x1 - x0) * (y1 - y0);

    // Benchmark bands (higher score = more creditworthy), interpolated inside each band.
    public static decimal LeverageScore(decimal x)
    {
        if (x < 1.5m) return Lerp(x, 0, 1.5m, 100, 95);
        if (x < 2.5m) return Lerp(x, 1.5m, 2.5m, 94, 80);
        if (x <= 4m) return Lerp(x, 2.5m, 4m, 79, 60);
        return Math.Max(20, 59 - (x - 4m) * 10);
    }

    public static decimal MarginScore(decimal m)
    {
        if (m > 0.20m) return Math.Min(100, Lerp(m, 0.20m, 0.30m, 90, 100));
        if (m >= 0.15m) return Lerp(m, 0.15m, 0.20m, 80, 89);
        if (m >= 0.10m) return Lerp(m, 0.10m, 0.15m, 70, 79);
        return Math.Max(0, 59 - (0.10m - m) * 300);
    }

    public static decimal DscrScore(decimal x)
    {
        if (x > 10) return Math.Min(100, Lerp(x, 10, 20, 95, 100));
        if (x >= 5) return Lerp(x, 5, 10, 80, 94);
        if (x >= 2) return Lerp(x, 2, 5, 60, 79);
        return Math.Max(0, 59 - (2 - x) * 20);
    }

    /// <summary>Variance = |tax - bank| / tax. Between 1% and 5% is an assumption (the benchmark only fixes the ends).</summary>
    public static decimal IntegrityScore(decimal variance)
    {
        if (variance < 0.01m) return Lerp(variance, 0, 0.01m, 100, 95);
        if (variance <= 0.05m) return Lerp(variance, 0.01m, 0.05m, 94, 70);
        return Math.Max(0, 69 - (variance - 0.05m) * 500);
    }

    /// <summary>
    /// Risk score = 100 - creditworthiness. Pillars without input data (DSCR, data integrity) are marked unavailable
    /// and the remaining weights are renormalised.
    /// </summary>
    public static ScorecardResult Compute(ExtractedMetrics m)
    {
        var d = EbitdaCalculator.Derive(m);
        decimal? dscr = m.DebtService is > 0 ? d.Ebitda / m.DebtService : null;
        decimal? variance = m.BankCredits is not null && m.GrossReceipts > 0
            ? Math.Abs(m.GrossReceipts - m.BankCredits.Value) / m.GrossReceipts : null;

        var pillars = new List<PillarScore>
        {
            new("Debt / EBITDA", 0.40m, $"{d.Leverage:0.00}x", LeverageScore(d.Leverage)),
            new("Debt service coverage", 0.30m, dscr is null ? "n/a" : $"{dscr:0.0}x", dscr is null ? null : DscrScore(dscr.Value)),
            new("EBITDA margin", 0.20m, $"{d.Margin * 100:0.0}%", MarginScore(d.Margin)),
            new("Data integrity", 0.10m, variance is null ? "n/a" : $"{variance * 100:0.00}% variance", variance is null ? null : IntegrityScore(variance.Value)),
        };
        var avail = pillars.Where(p => p.Score is not null).ToList();
        var wsum = avail.Sum(p => p.Weight);
        var cred = avail.Sum(p => p.Score!.Value * p.Weight) / wsum;
        return new ScorecardResult(Math.Round(100 - cred, 1), Math.Round(cred, 1), pillars, d);
    }
}
