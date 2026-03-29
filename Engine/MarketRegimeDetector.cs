namespace CryptoBotStockSharp.Engine;

// ══════════════════════════════════════════════════════════════
//  Market Regime Definitions
// ══════════════════════════════════════════════════════════════

public enum MarketRegime
{
    Unknown,
    Compressed,    // BB tight + ATR low + ADX low → breakout imminent
    Ranging,       // ADX low, oscillating price → mean-reversion works
    TrendingBull,  // ADX high, +DI > -DI, EMA aligned → trend-following works
    TrendingBear,  // ADX high, -DI > +DI, EMA aligned → trend-following works
    Volatile       // ATR spike, wide BB → high uncertainty, reduce size
}

/// <summary>
/// Regime-adjusted parameters that MetaDecisionEngine uses to
/// reweight brains and adjust thresholds per market condition.
/// </summary>
public class RegimeInfo
{
    public MarketRegime Regime { get; set; } = MarketRegime.Unknown;
    public double Strength { get; set; }           // 0.0–1.0 how clearly we're in this regime
    public int Duration { get; set; }              // candles in current regime
    public double Brain1WeightMult { get; set; } = 1.0;  // multiplier for Brain1 (mean-reversion)
    public double Brain2WeightMult { get; set; } = 1.0;  // multiplier for Brain2 (trend-following)
    public double BuyThresholdMult { get; set; } = 1.0;  // <1 = easier to buy, >1 = harder
    public double SellThresholdMult { get; set; } = 1.0; // <1 = easier to sell, >1 = harder
    public double ConfidenceAdj { get; set; }      // added to avg confidence
    public string Summary { get; set; } = "";

    // Diagnostic fields
    public double BbWidthPctl { get; set; }
    public double AtrPctl { get; set; }
    public double EffectiveAdx { get; set; }
    public double AdxSlope { get; set; }
    public double RawAdx { get; set; }
}

/// <summary>
/// Detects the current market regime by analyzing indicator convergence.
/// Solves the problem where mean-reversion (Brain1) and trend-following (Brain2)
/// cancel each other out by telling MetaDecisionEngine which brain to trust.
/// 
/// Uses rolling percentiles of BB width and ATR to adapt to each asset's
/// volatility profile, ADX slope to detect stale trends, and coherence
/// checks to validate that trend signals match actual price behavior.
/// </summary>
public class MarketRegimeDetector
{
    private readonly int _lookback;
    private readonly int _minHistory;

    // ── Rolling history for percentile calculations ──
    private readonly List<double> _bbWidthHistory = new();
    private readonly List<double> _atrPctHistory = new();
    private readonly List<double> _adxHistory = new();

    // ── Persistence / anti-flicker ──
    private MarketRegime _currentRegime = MarketRegime.Unknown;
    private double _currentStrength;
    private int _regimeDuration;
    private const double FlickerThreshold = 0.15;   // new regime must beat current by this margin
    private const double MinRegimeStrength = 0.35;   // below this → Unknown

    // ── ADX slope tracking ──
    private readonly List<double> _recentAdx = new();
    private const int AdxSlopeWindow = 5;

    public bool IsReady => _bbWidthHistory.Count >= _minHistory;
    public MarketRegime CurrentRegime => _currentRegime;

    public MarketRegimeDetector(int lookback = 50, int minHistory = 20)
    {
        _lookback = lookback;
        _minHistory = minHistory;
    }

    // ══════════════════════════════════════════════════════════════
    //  MAIN UPDATE — called every candle after both brains update
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Update regime detection with latest indicator values.
    /// Call after both Brain1 and Brain2 have processed the candle.
    /// </summary>
    public RegimeInfo Update(
        double bbWidth,        // from TechnicalIndicators.BollingerWidth
        double atrPercent,     // ATR / price * 100 from StockSharpBrain
        double rawAdx,         // from StockSharpBrain.Adx
        double plusDi,         // from StockSharpBrain.PlusDi
        double minusDi,        // from StockSharpBrain.MinusDi
        double rsi,            // from TechnicalIndicators.Rsi
        bool emaFastAboveSlow  // from TechnicalIndicators.EmaFastAboveSlow
    )
    {
        // ── Accumulate history ──
        AddToRolling(_bbWidthHistory, bbWidth);
        AddToRolling(_atrPctHistory, atrPercent);
        AddToRolling(_adxHistory, rawAdx);

        // Track recent ADX for slope calculation
        _recentAdx.Add(rawAdx);
        if (_recentAdx.Count > AdxSlopeWindow + 2)
            _recentAdx.RemoveAt(0);

        if (!IsReady)
        {
            return new RegimeInfo
            {
                Regime = MarketRegime.Unknown,
                Strength = 0,
                Summary = $"Warming up ({_bbWidthHistory.Count}/{_minHistory})"
            };
        }

        // ── Calculate percentiles ──
        double bbPctl = GetPercentile(_bbWidthHistory, bbWidth);
        double atrPctl = GetPercentile(_atrPctHistory, atrPercent);

        // ── ADX slope (positive = strengthening, negative = weakening) ──
        double adxSlope = CalcAdxSlope();

        // ── Effective ADX: penalize when ADX is high but volatility is dead ──
        double effectiveAdx = CalcEffectiveAdx(rawAdx, bbPctl, atrPctl, adxSlope);

        // ── Score each regime ──
        var scores = new Dictionary<MarketRegime, double>
        {
            [MarketRegime.Compressed] = Clamp01(ScoreCompressed(bbPctl, atrPctl, rawAdx)),
            [MarketRegime.Ranging] = Clamp01(ScoreRanging(effectiveAdx, rawAdx, bbPctl, atrPctl, rsi, adxSlope)),
            [MarketRegime.TrendingBull] = Clamp01(ScoreTrendingBull(effectiveAdx, plusDi, minusDi, emaFastAboveSlow, bbPctl, atrPctl, adxSlope)),
            [MarketRegime.TrendingBear] = Clamp01(ScoreTrendingBear(effectiveAdx, plusDi, minusDi, emaFastAboveSlow, bbPctl, atrPctl, adxSlope)),
            [MarketRegime.Volatile] = Clamp01(ScoreVolatile(bbPctl, atrPctl, rawAdx))
        };

        // ── Find best regime ──
        var bestRegime = scores.OrderByDescending(kv => kv.Value).First();
        var newRegime = bestRegime.Key;
        double newStrength = bestRegime.Value;

        // ── Anti-flicker: require margin to switch ──
        if (newRegime != _currentRegime)
        {
            double currentScore = scores.GetValueOrDefault(_currentRegime, 0);
            bool strongEnough = newStrength >= MinRegimeStrength;
            bool clearMargin = (newStrength - currentScore) >= FlickerThreshold;

            if (!strongEnough || !clearMargin)
            {
                // Stick with current regime
                newRegime = _currentRegime;
                newStrength = _currentStrength;
            }
        }

        // ── Update persistence ──
        if (newRegime == _currentRegime)
        {
            _regimeDuration++;
        }
        else
        {
            _regimeDuration = 1;
            _currentRegime = newRegime;
        }
        _currentStrength = newStrength;

        // ── Build regime info with weight/threshold adjustments ──
        var info = BuildRegimeInfo(newRegime, newStrength, effectiveAdx);
        info.Duration = _regimeDuration;
        info.BbWidthPctl = bbPctl;
        info.AtrPctl = atrPctl;
        info.EffectiveAdx = effectiveAdx;
        info.AdxSlope = adxSlope;
        info.RawAdx = rawAdx;
        info.Summary = $"{newRegime}(str={newStrength:F2}, dur={_regimeDuration}) " +
                       $"BB%={bbPctl:F0} ATR%={atrPctl:F0} " +
                       $"eADX={effectiveAdx:F1}(raw={rawAdx:F1}, slope={adxSlope:F2}) " +
                       $"+DI={plusDi:F1} -DI={minusDi:F1}";

        return info;
    }

    // ══════════════════════════════════════════════════════════════
    //  REGIME SCORING  (each returns 0.0–1.0+)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Compressed: BB squeezed + ATR low + ADX weak → breakout imminent.
    /// Both brains get equal weight, thresholds lowered to catch breakout early.
    /// </summary>
    private double ScoreCompressed(double bbPctl, double atrPctl, double rawAdx)
    {
        double score = 0;

        // BB width in bottom 20th percentile → strong compression signal
        if (bbPctl < 10) score += 0.50;
        else if (bbPctl < 20) score += 0.35;
        else if (bbPctl < 30) score += 0.15;

        // ATR in bottom 20th percentile → volatility dried up
        if (atrPctl < 10) score += 0.30;
        else if (atrPctl < 20) score += 0.20;
        else if (atrPctl < 30) score += 0.10;

        // Low ADX confirms no trend
        if (rawAdx < 15) score += 0.20;
        else if (rawAdx < 20) score += 0.10;

        return score;
    }

    /// <summary>
    /// Ranging: ADX low, price oscillating → Brain1 (mean-reversion) excels.
    /// Also detects "stale trend" where ADX is high but declining with no volatility.
    /// </summary>
    private double ScoreRanging(double effectiveAdx, double rawAdx,
                                 double bbPctl, double atrPctl,
                                 double rsi, double adxSlope)
    {
        double score = 0;

        // Low effective ADX → no real trend
        if (effectiveAdx < 20) score += 0.40;
        else if (effectiveAdx < 25) score += 0.25;
        else if (effectiveAdx < 30) score += 0.10;

        // Moderate BB width (not compressed, not wide)
        if (bbPctl >= 25 && bbPctl <= 65) score += 0.15;

        // Moderate ATR
        if (atrPctl >= 20 && atrPctl <= 60) score += 0.10;

        // RSI near neutral → oscillating
        if (rsi >= 40 && rsi <= 60) score += 0.15;
        else if (rsi >= 35 && rsi <= 65) score += 0.05;

        // ADX declining → trend weakening
        if (adxSlope < -0.3) score += 0.10;

        // ── Stale trend detection ──
        // High raw ADX but heavily penalized = stale trend → actually ranging
        double staleGap = rawAdx - effectiveAdx;
        if (staleGap > 20 && (bbPctl < 20 || atrPctl < 20))
            score += 0.25;
        else if (staleGap > 15 && bbPctl < 40 && atrPctl < 50)
            score += 0.15;
        else if (staleGap > 10 && (bbPctl < 30 || atrPctl < 30))
            score += 0.10;

        return score;
    }

    /// <summary>
    /// TrendingBull: strong uptrend confirmed by ADX, DI, and EMA alignment.
    /// Brain2 (trend-following) gets higher weight.
    /// Coherence check: penalize if volatility metrics don't support the trend.
    /// </summary>
    private double ScoreTrendingBull(double effectiveAdx, double plusDi, double minusDi,
                                      bool emaFastAboveSlow,
                                      double bbPctl, double atrPctl, double adxSlope)
    {
        double score = 0;

        // ADX above threshold → trend exists
        if (effectiveAdx > 35) score += 0.35;
        else if (effectiveAdx > 25) score += 0.25;
        else if (effectiveAdx > 20) score += 0.10;

        // +DI dominates -DI → bullish direction
        double diSum = plusDi + minusDi;
        if (diSum > 0)
        {
            double diGap = (plusDi - minusDi) / diSum;
            if (diGap > 0.3) score += 0.30;
            else if (diGap > 0.15) score += 0.20;
            else if (diGap > 0.05) score += 0.10;
        }

        // EMA alignment confirms
        if (emaFastAboveSlow) score += 0.15;

        // ── Coherence penalty: trend signal but no volatility to back it up ──
        if (bbPctl < 20) score -= 0.25;
        if (atrPctl < 20) score -= 0.25;

        // ADX declining → trend weakening
        if (adxSlope < -0.5) score -= 0.15;
        else if (adxSlope < -0.3) score -= 0.05;

        return Math.Max(score, 0);
    }

    /// <summary>
    /// TrendingBear: strong downtrend — mirror of TrendingBull with -DI dominant.
    /// </summary>
    private double ScoreTrendingBear(double effectiveAdx, double plusDi, double minusDi,
                                      bool emaFastAboveSlow,
                                      double bbPctl, double atrPctl, double adxSlope)
    {
        double score = 0;

        if (effectiveAdx > 35) score += 0.35;
        else if (effectiveAdx > 25) score += 0.25;
        else if (effectiveAdx > 20) score += 0.10;

        double diSum = plusDi + minusDi;
        if (diSum > 0)
        {
            double diGap = (minusDi - plusDi) / diSum;  // note: reversed
            if (diGap > 0.3) score += 0.30;
            else if (diGap > 0.15) score += 0.20;
            else if (diGap > 0.05) score += 0.10;
        }

        // EMA alignment: fast below slow confirms bear
        if (!emaFastAboveSlow) score += 0.15;

        // Coherence penalties
        if (bbPctl < 20) score -= 0.25;
        if (atrPctl < 20) score -= 0.25;
        if (adxSlope < -0.5) score -= 0.15;
        else if (adxSlope < -0.3) score -= 0.05;

        return Math.Max(score, 0);
    }

    /// <summary>
    /// Volatile: ATR spiking + wide BB → high uncertainty.
    /// Reduce confidence in both brains, raise thresholds.
    /// </summary>
    private double ScoreVolatile(double bbPctl, double atrPctl, double rawAdx)
    {
        double score = 0;

        // ATR in top percentiles → volatility spike
        if (atrPctl > 90) score += 0.50;
        else if (atrPctl > 80) score += 0.35;
        else if (atrPctl > 70) score += 0.20;

        // Wide BB confirms
        if (bbPctl > 85) score += 0.30;
        else if (bbPctl > 70) score += 0.15;

        // High ADX can accompany volatile moves
        if (rawAdx > 40) score += 0.10;

        return score;
    }

    // ══════════════════════════════════════════════════════════════
    //  REGIME → WEIGHT/THRESHOLD CONFIGURATION
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Build regime-specific parameters. Weight multipliers are interpolated
    /// by regime strength so weak detections don't cause drastic changes.
    /// </summary>
    private RegimeInfo BuildRegimeInfo(MarketRegime regime, double strength, double effectiveAdx)
    {
        // Raw target multipliers for each regime
        double b1Mult, b2Mult, buyThMult, sellThMult, confAdj;

        switch (regime)
        {
            case MarketRegime.Compressed:
                // Equal weight, lower thresholds to catch breakout early
                b1Mult = 1.0;
                b2Mult = 1.0;
                buyThMult = 0.80;
                sellThMult = 0.80;
                confAdj = 0.05;
                break;

            case MarketRegime.Ranging:
                // Favor Brain1 (mean-reversion), neutral thresholds
                b1Mult = 1.35;
                b2Mult = 0.65;
                buyThMult = 1.0;
                sellThMult = 1.0;
                confAdj = 0.0;
                break;

            case MarketRegime.TrendingBull:
                // Favor Brain2 (trend-following), easier buys, harder sells
                b1Mult = 0.65;
                b2Mult = 1.50;
                buyThMult = 0.85;    // lower → easier to buy into uptrend
                sellThMult = 1.15;   // higher → hold positions longer
                confAdj = 0.10;
                break;

            case MarketRegime.TrendingBear:
                // Favor Brain2, harder buys, easier sells
                b1Mult = 0.65;
                b2Mult = 1.50;
                buyThMult = 1.20;    // higher → harder to buy into downtrend
                sellThMult = 0.85;   // lower → exit positions sooner
                confAdj = 0.10;
                break;

            case MarketRegime.Volatile:
                // Reduce both, raise all thresholds
                b1Mult = 0.85;
                b2Mult = 0.85;
                buyThMult = 1.25;
                sellThMult = 1.25;
                confAdj = -0.10;
                break;

            default:
                return new RegimeInfo { Regime = regime, Strength = 0 };
        }

        // ── Interpolate by strength: weak regime → parameters stay near 1.0 ──
        double s = Math.Max(0, Math.Min(1.0, strength));

        return new RegimeInfo
        {
            Regime = regime,
            Strength = strength,
            Brain1WeightMult = Lerp(1.0, b1Mult, s),
            Brain2WeightMult = Lerp(1.0, b2Mult, s),
            BuyThresholdMult = Lerp(1.0, buyThMult, s),
            SellThresholdMult = Lerp(1.0, sellThMult, s),
            ConfidenceAdj = confAdj * s
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Calculate effective ADX: penalizes when raw ADX is high but
    /// volatility has dried up (stale trend from historical data).
    /// </summary>
    private double CalcEffectiveAdx(double rawAdx, double bbPctl, double atrPctl, double adxSlope)
    {
        double penalty = 1.0;

        // If BB and ATR are both low but ADX is high → stale trend
        if (rawAdx > 30)
        {
            if (bbPctl < 25) penalty -= 0.20;
            if (atrPctl < 25) penalty -= 0.20;

            // ADX declining compounds the staleness
            if (adxSlope < -0.5) penalty -= 0.10;
            if (adxSlope < -1.0) penalty -= 0.10;

            // Extreme ADX plateauing = stale regardless of ATR history
            if (rawAdx > 60 && Math.Abs(adxSlope) < 0.5)
                penalty -= 0.15;
            
            // Very tight BB with high ADX = almost certainly stale
            if (rawAdx > 50 && bbPctl < 35)
                penalty -= 0.10;
        }

        penalty = Math.Max(0.3, penalty);  // floor at 30% of raw value
        return rawAdx * penalty;
    }

    /// <summary>
    /// Calculate ADX slope using linear regression over recent values.
    /// Positive = trend strengthening, negative = trend weakening.
    /// </summary>
    private double CalcAdxSlope()
    {
        if (_recentAdx.Count < 3) return 0;

        var values = _recentAdx.TakeLast(AdxSlopeWindow).ToList();
        int n = values.Count;
        if (n < 3) return 0;

        // Simple linear regression: slope = Σ((x-x̄)(y-ȳ)) / Σ((x-x̄)²)
        double xMean = (n - 1) / 2.0;
        double yMean = values.Average();
        double num = 0, den = 0;

        for (int i = 0; i < n; i++)
        {
            double dx = i - xMean;
            double dy = values[i] - yMean;
            num += dx * dy;
            den += dx * dx;
        }

        return den > 0 ? num / den : 0;
    }

    /// <summary>
    /// Calculate what percentile the current value falls in relative to history.
    /// Returns 0–100.
    /// </summary>
    private double GetPercentile(List<double> history, double value)
    {
        if (history.Count == 0) return 50;
        int below = history.Count(v => v < value);
        return (below * 100.0) / history.Count;
    }

    /// <summary>
    /// Add value to rolling history, trimming to lookback window.
    /// </summary>
    private void AddToRolling(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > _lookback)
            history.RemoveAt(0);
    }

    /// <summary>
    /// P2 Fix: Clamp value to [0.0, 1.0].
    /// Applied to all regime scores before comparison to ensure fair ranking.
    /// </summary>
    private static double Clamp01(double value) => Math.Max(0.0, Math.Min(1.0, value));

    /// <summary>
    /// Linear interpolation: returns a when t=0, b when t=1.
    /// </summary>
    private static double Lerp(double a, double b, double t)
    {
        return a + (b - a) * t;
    }
}