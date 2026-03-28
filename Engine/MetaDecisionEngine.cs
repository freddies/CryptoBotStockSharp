using CryptoBotStockSharp.Models;

namespace CryptoBotStockSharp.Engine;

/// <summary>
/// Combines signals from multiple brains into a final trading decision.
/// Each brain votes with a score and confidence level.
/// Now accepts optional RegimeInfo to dynamically adjust brain weights
/// and thresholds based on market conditions.
/// </summary>
public class MetaDecisionEngine
{
    private readonly BotConfig _config;

    public MetaDecisionEngine(BotConfig config)
    {
        _config = config;
    }

    public MetaDecision Decide(
        TradeSignal technicalSignal,
        StockSharpSignal? brain2Signal,
        RegimeInfo? regime = null)
    {
        // ── Regime-adjusted brain weights ──
        double brain1Weight = _config.WeightBrainTechnical;
        double brain2Weight = _config.WeightBrainStockSharp;

        if (regime != null && regime.Regime != MarketRegime.Unknown)
        {
            brain1Weight *= regime.Brain1WeightMult;
            brain2Weight *= regime.Brain2WeightMult;

            // Renormalize so weights still sum to ~1.0
            double wSum = brain1Weight + brain2Weight;
            if (wSum > 0)
            {
                brain1Weight /= wSum;
                brain2Weight /= wSum;
            }
        }

        var votes = new List<BrainVote>();

        // ── Brain 1: Technical indicators (RSI, EMA, BB, MACD) ──
        double techConfidence = Math.Abs(technicalSignal.CompositeScore) > 0.4 ? 0.8 : 0.5;
        votes.Add(new BrainVote
        {
            Name = "Technical",
            Score = technicalSignal.CompositeScore,
            Confidence = techConfidence,
            Weight = brain1Weight
        });

        // ── Brain 2: StockSharp (Stoch, ADX, ATR, OBV, W%R) ──
        if (brain2Signal != null && brain2Signal.Confidence > 0)
        {
            votes.Add(new BrainVote
            {
                Name = "StockSharp",
                Score = brain2Signal.Score,
                Confidence = brain2Signal.Confidence,
                Weight = brain2Weight
            });
        }

        // ── Weighted consensus calculation ──
        double totalEffectiveWeight = 0;
        double weightedScore = 0;
        double totalConfidence = 0;

        foreach (var vote in votes)
        {
            double effectiveWeight = vote.Weight * vote.Confidence;
            weightedScore += vote.Score * effectiveWeight;
            totalConfidence += vote.Confidence * vote.Weight;
            totalEffectiveWeight += effectiveWeight;
        }

        if (totalEffectiveWeight > 0)
            weightedScore /= totalEffectiveWeight;

        double avgConfidence = votes.Sum(v => v.Weight) > 0
            ? totalConfidence / votes.Sum(v => v.Weight)
            : 0;

        // ── Apply regime confidence adjustment ──
        if (regime != null && regime.Regime != MarketRegime.Unknown)
        {
            avgConfidence = Math.Max(0, Math.Min(1.0, avgConfidence + regime.ConfidenceAdj));
        }

        // ── Check if brains agree on direction ──
        bool allBullish = votes.All(v => v.Score > 0);
        bool allBearish = votes.All(v => v.Score < 0);
        bool consensus = votes.Count >= 2 && (allBullish || allBearish);

        // ── Calculate thresholds ──
        double buyThreshold = _config.BuyScoreThreshold;
        double sellThreshold = _config.SellScoreThreshold;

        // Apply regime threshold adjustments
        if (regime != null && regime.Regime != MarketRegime.Unknown)
        {
            buyThreshold *= regime.BuyThresholdMult;
            sellThreshold *= regime.SellThresholdMult;
        }

        // Require higher score when brains disagree
        if (!consensus)
        {
            buyThreshold *= 1.15;
            sellThreshold *= 1.15;
        }

        // ── Build decision ──
        var decision = new MetaDecision
        {
            FinalScore = weightedScore,
            Confidence = avgConfidence,
            Consensus = consensus,
            BrainCount = votes.Count,
        };

        // Build reasoning string
        var voteStrings = votes.Select(v =>
            $"{v.Name}={v.Score:F3}(conf={v.Confidence:F2},w={v.Weight:F2})");
        decision.Reasoning = $"[{string.Join(" | ", voteStrings)}] " +
            $"→ Final={weightedScore:F3} " +
            $"(consensus={consensus}, threshold={buyThreshold:F3})";

        // Add regime info
        if (regime != null && regime.Regime != MarketRegime.Unknown)
        {
            decision.MarketRegime = regime.Regime.ToString();
            decision.RegimeStrength = regime.Strength;
            decision.Reasoning += $"\n  🌍 Regime: {regime.Summary}";
        }

        // Add Brain 2 details
        if (brain2Signal != null)
        {
            decision.Reasoning += $"\n  📊 Brain2: {brain2Signal.Summary}";
            decision.TrendDirection = brain2Signal.TrendDirection;
            decision.IsStrongTrend = brain2Signal.IsStrongTrend;
            decision.Volatility = brain2Signal.Volatility;
            decision.AtrValue = brain2Signal.AtrValue;
        }

        // ── Final action ──
        if (weightedScore >= buyThreshold && avgConfidence >= 0.3)
        {
            decision.Action = SignalType.Buy;
        }
        else if (weightedScore <= sellThreshold && avgConfidence >= 0.3)
        {
            decision.Action = SignalType.Sell;
        }
        else
        {
            decision.Action = SignalType.None;
        }

        return decision;
    }
}

public class BrainVote
{
    public string Name { get; set; } = "";
    public double Score { get; set; }
    public double Confidence { get; set; }
    public double Weight { get; set; }
}

public class MetaDecision
{
    public SignalType Action { get; set; }
    public double FinalScore { get; set; }
    public double Confidence { get; set; }
    public bool Consensus { get; set; }
    public int BrainCount { get; set; }
    public string Reasoning { get; set; } = "";
    public string TrendDirection { get; set; } = "";
    public bool IsStrongTrend { get; set; }
    public double Volatility { get; set; }
    public double AtrValue { get; set; }

    // ── Regime fields ──
    public string MarketRegime { get; set; } = "";
    public double RegimeStrength { get; set; }
}