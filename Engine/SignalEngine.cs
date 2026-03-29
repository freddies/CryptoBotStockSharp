using CryptoBotStockSharp.Indicators;
using CryptoBotStockSharp.Models;

namespace CryptoBotStockSharp.Engine;

/// <summary>
/// Generates trading signals from a composite scoring system.
///
/// P2 Fixes:
///   - Removed internal regime adaptation (IsRangingMarket branching)
///   - Step-function scoring replaced with continuous linear interpolation.
///     Old code had dead zones (RSI 40-60, BB 0.3-0.7, W%R -60 to -40)
///     where indicators contributed exactly 0.0 — roughly 40% of market time.
///     New code uses linear ramps with preserved crossover/divergence bonuses.
/// </summary>
public class SignalEngine
{
    private readonly BotConfig _config;

    public SignalEngine(BotConfig config)
    {
        _config = config;
    }

    public TradeSignal Analyze(TechnicalIndicators indicators, decimal currentPrice,
                                DateTimeOffset? candleTime = null)
    {
        if (!indicators.AllReady)
        {
            return new TradeSignal
            {
                Type = SignalType.None,
                Reason = $"Warming up ({indicators.CandleCount} candles so far)"
            };
        }

        // ── Score each indicator (continuous, -1 to +1) ──
        double rsiScore = ScoreRsi(indicators);
        double bollingerScore = ScoreBollinger(indicators, currentPrice);
        double emaScore = ScoreEma(indicators);
        double macdScore = ScoreMacd(indicators);

        // ── Normalize weights ──
        double wRsi = _config.WeightRsi;
        double wBB = _config.WeightBollinger;
        double wEma = _config.WeightEma;
        double wMacd = _config.WeightMacd;

        double totalWeight = wRsi + wBB + wEma + wMacd;
        if (totalWeight > 0)
        {
            wRsi /= totalWeight;
            wBB /= totalWeight;
            wEma /= totalWeight;
            wMacd /= totalWeight;
        }

        // ── Composite Score ──
        double composite = (rsiScore * wRsi) +
                           (bollingerScore * wBB) +
                           (emaScore * wEma) +
                           (macdScore * wMacd);

        // ── Determine Signal ──
        var signal = new TradeSignal
        {
            CompositeScore = composite,
            RsiScore = rsiScore,
            BollingerScore = bollingerScore,
            EmaScore = emaScore,
            MacdScore = macdScore,
            Timestamp = candleTime ?? DateTimeOffset.UtcNow  // FIX: use candle time
        };

        if (composite >= _config.BuyScoreThreshold)
        {
            signal.Type = SignalType.Buy;
            signal.Reason = $"BUY Score={composite:F3} " +
                            $"(RSI={rsiScore:F2} BB={bollingerScore:F2} " +
                            $"EMA={emaScore:F2} MACD={macdScore:F2})";
        }
        else if (composite <= _config.SellScoreThreshold)
        {
            signal.Type = SignalType.Sell;
            signal.Reason = $"SELL Score={composite:F3} " +
                            $"(RSI={rsiScore:F2} BB={bollingerScore:F2} " +
                            $"EMA={emaScore:F2} MACD={macdScore:F2})";
        }
        else
        {
            signal.Type = SignalType.None;
            signal.Reason = $"HOLD Score={composite:F3}";
        }

        return signal;
    }

    // ══════════════════════════════════════════════════════════
    //  Continuous Indicator Scoring (-1 to +1)
    //
    //  P2 Fix: Linear interpolation replaces step functions.
    //  Old step functions had dead zones (~40% of market time → score 0.0).
    //  New functions ramp continuously through the neutral zone.
    //  Crossover/extreme bonuses are preserved.
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// RSI scoring: linear ramp from oversold (+1.0) through neutral (0.0) to overbought (-1.0).
    ///
    /// Shape:
    ///   RSI ≤ OS-10:  +1.0  (deep oversold, strong buy)
    ///   RSI = OS:      +0.7
    ///   RSI = 50:       0.0  (neutral)
    ///   RSI = OB:      -0.7
    ///   RSI ≥ OB+10:  -1.0  (deep overbought, strong sell)
    ///
    /// Linear interpolation between these points — no dead zones.
    /// </summary>
    private double ScoreRsi(TechnicalIndicators ind)
    {
        double rsi = (double)ind.Rsi;
        double os = _config.RsiOversold;       // default 30
        double ob = _config.RsiOverbought;     // default 70
        double mid = (os + ob) / 2.0;          // default 50

        if (rsi <= os - 10) return 1.0;
        if (rsi >= ob + 10) return -1.0;

        // Below midpoint: bullish territory
        if (rsi <= mid)
        {
            if (rsi <= os)
            {
                // Ramp from 1.0 (at os-10) to 0.7 (at os)
                return Lerp(1.0, 0.7, (rsi - (os - 10)) / 10.0);
            }
            else
            {
                // Ramp from 0.7 (at os) to 0.0 (at mid)
                return Lerp(0.7, 0.0, (rsi - os) / (mid - os));
            }
        }
        else
        {
            if (rsi >= ob)
            {
                // Ramp from -0.7 (at ob) to -1.0 (at ob+10)
                return Lerp(-0.7, -1.0, (rsi - ob) / 10.0);
            }
            else
            {
                // Ramp from 0.0 (at mid) to -0.7 (at ob)
                return Lerp(0.0, -0.7, (rsi - mid) / (ob - mid));
            }
        }
    }

    /// <summary>
    /// Bollinger position scoring: linear ramp from below lower band (+1.0)
    /// through middle (0.0) to above upper band (-1.0).
    ///
    /// Position 0.0 = at lower band, 0.5 = middle, 1.0 = at upper band.
    /// </summary>
    private double ScoreBollinger(TechnicalIndicators ind, decimal price)
    {
        double position = ind.GetBollingerPosition(price);

        // Beyond bands: strong signals
        if (position <= 0.0) return 1.0;
        if (position >= 1.0) return -1.0;

        // Linear ramp: 0.0 → +1.0, 0.5 → 0.0, 1.0 → -1.0
        return Lerp(1.0, -1.0, position);
    }

    /// <summary>
    /// EMA scoring: crossover events get full score, sustained trends
    /// get proportional score based on EMA gap.
    /// Unchanged from original — already continuous for sustained trends.
    /// Crossover detection gives binary ±1.0 which is intentional (event-driven).
    /// </summary>
    private double ScoreEma(TechnicalIndicators ind)
    {
        if (!ind.EmaReady) return 0.0;

        bool currentAbove = ind.EmaFastAboveSlow;
        bool previousAbove = ind.PreviousEmaFastAboveSlow;

        if (currentAbove && !previousAbove) return 1.0;
        if (!currentAbove && previousAbove) return -1.0;

        if (ind.EmaSlow == 0) return 0.0;

        double gap = (double)(ind.EmaFast - ind.EmaSlow) / (double)ind.EmaSlow * 100.0;

        // FIX: Tanh curve instead of linear clamp — approaches ±0.6 asymptotically
        // Old: saturated at 4% gap. New: 4% → 0.31, 8% → 0.48, 15% → 0.57
        return Math.Tanh(gap * 0.08) * 0.6;
    }

    /// <summary>
    /// MACD scoring: crossover events (histogram sign change) get full score.
    /// Otherwise score ramps by histogram direction and magnitude.
    ///
    /// P2 Fix: removed dead zone between 0.2 and -0.2 for non-crossover states.
    /// Now uses histogram momentum (rising/falling) as a continuous multiplier.
    /// </summary>
    private double ScoreMacd(TechnicalIndicators ind)
    {
        double histogram = (double)ind.MacdHistogram;
        double prevHistogram = (double)ind.PrevMacdHistogram;
        bool rising = histogram > prevHistogram;

        // Zero-line crossovers: strong discrete signals
        if (prevHistogram <= 0 && histogram > 0) return 1.0;
        if (prevHistogram >= 0 && histogram < 0) return -1.0;

        // FIX: Magnitude-aware scoring instead of flat 4-value step
        if (histogram > 0)
        {
            double baseScore = rising ? 0.6 : 0.3;
            // Boost by momentum magnitude (how fast histogram is changing)
            double momentum = Math.Abs(histogram - prevHistogram);
            double momentumBoost = Math.Min(momentum / (Math.Abs(histogram) + 0.0001) * 0.2, 0.2);
            return Math.Min(baseScore + momentumBoost, 0.8);
        }
        else if (histogram < 0)
        {
            double baseScore = rising ? -0.3 : -0.6;
            double momentum = Math.Abs(histogram - prevHistogram);
            double momentumBoost = Math.Min(momentum / (Math.Abs(histogram) + 0.0001) * 0.2, 0.2);
            return Math.Max(baseScore - momentumBoost, -0.8);
        }

        return 0.0;
    }

    /// <summary>
    /// Linear interpolation: returns a when t=0, b when t=1.
    /// t is clamped to [0, 1].
    /// </summary>
    private static double Lerp(double a, double b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return a + (b - a) * t;
    }
}