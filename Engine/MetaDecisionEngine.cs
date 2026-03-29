using CryptoBotStockSharp.Models;

namespace CryptoBotStockSharp.Engine;

/// <summary>
/// Combines signals from multiple brains into a final trading decision.
///
/// P0 Fixes:
///   - Binary consensus replaced with weighted directional alignment.
///     Old: required ALL brains to agree on sign → blocked all trend trades
///     New: measures weight-adjusted directional dominance (0–1 scale)
///   - Regime override: in strong TrendingBull/Bear, consensus forced true
///     so the regime-favored brain can act without unanimous agreement
///   - Brain1 confidence: continuous scaling replaces binary 0.5/0.8 step.
///     Regime damping reduces Brain1 confidence when it contradicts a clear trend.
///   - No-consensus threshold penalty: scaled by disagreement magnitude
///     and reduced in strong regimes (was flat 1.15× multiplier)
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

            double wSum = brain1Weight + brain2Weight;
            if (wSum > 0)
            {
                brain1Weight /= wSum;
                brain2Weight /= wSum;
            }
        }

        var votes = new List<BrainVote>();

        // ══════════════════════════════════════════════════════
        //  P0 FIX: Brain 1 confidence — continuous + regime damping
        //
        //  Old: binary step function (0.5 or 0.8)
        //  New: linear ramp 0.3→0.85 based on signal magnitude,
        //       with 40% damping when Brain1 contradicts a strong regime.
        //
        //  This prevents a mildly-overbought RSI reading (score=-0.2,
        //  conf=0.5) from blocking a strong trend signal from Brain2.
        // ══════════════════════════════════════════════════════

        double absScore = Math.Abs(technicalSignal.CompositeScore);
        double techConfidence;

        if (absScore > 0.6)
            techConfidence = 0.85;
        else if (absScore > 0.1)
            techConfidence = 0.4 + (absScore / 0.6) * 0.45;
        else
            techConfidence = 0.3;

        // Dampen Brain1 confidence when it contradicts a clear regime
        if (regime != null && regime.Strength >= 0.6)
        {
            bool brain1AgreesTrend =
                (regime.Regime == MarketRegime.TrendingBull && technicalSignal.CompositeScore > 0) ||
                (regime.Regime == MarketRegime.TrendingBear && technicalSignal.CompositeScore < 0);
            if (!brain1AgreesTrend)
                techConfidence *= 0.6;
        }

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
        double totalWeight = 0;  // FIX: cache this

        foreach (var vote in votes)
        {
            double effectiveWeight = vote.Weight * vote.Confidence;
            weightedScore += vote.Score * effectiveWeight;
            totalConfidence += vote.Confidence * vote.Weight;
            totalEffectiveWeight += effectiveWeight;
            totalWeight += vote.Weight;  // FIX: accumulate once
        }

        if (totalEffectiveWeight > 0)
            weightedScore /= totalEffectiveWeight;

        // FIX: Use cached totalWeight instead of re-iterating
        double avgConfidence = totalWeight > 0
            ? totalConfidence / totalWeight
            : 0;

        // ── Apply regime confidence adjustment ──
        if (regime != null && regime.Regime != MarketRegime.Unknown)
        {
            avgConfidence = Math.Max(0, Math.Min(1.0, avgConfidence + regime.ConfidenceAdj));
        }

        // ══════════════════════════════════════════════════════
        //  P0 FIX: Weighted directional alignment replaces binary consensus
        //
        //  Old: consensus = all brains have same sign
        //       → Brain1=-0.1, Brain2=+0.4 → signs differ → consensus=false
        //       → threshold inflated 15% → trade blocked
        //
        //  New: directionalAlignment measures how strongly the weighted
        //       votes lean one direction (0=split, 1=unanimous).
        //       Consensus is true when alignment > 0.3.
        //
        //  Regime override: in strong TrendingBull/Bear (str≥0.6),
        //  consensus is forced true. The regime already tells us which
        //  brain to trust via weight multipliers.
        // ══════════════════════════════════════════════════════

        double directionalAlignment = 0;
        if (votes.Count >= 2)
        {
            double bullWeight = votes.Where(v => v.Score > 0)
                .Sum(v => v.Weight * v.Confidence);
            double bearWeight = votes.Where(v => v.Score < 0)
                .Sum(v => v.Weight * v.Confidence);
            double totalDirectionalWeight = bullWeight + bearWeight;

            if (totalDirectionalWeight > 0)
                directionalAlignment = Math.Abs(bullWeight - bearWeight)
                                       / totalDirectionalWeight;
        }

        bool consensus = votes.Count >= 2 && directionalAlignment > 0.3;

        // Regime override: strong trend → trust the regime-weighted result
        if (regime != null &&
            (regime.Regime == MarketRegime.TrendingBull ||
             regime.Regime == MarketRegime.TrendingBear) &&
            regime.Strength >= 0.6)
        {
            consensus = true;
        }

        // ── Calculate thresholds ──
        double buyThreshold = _config.BuyScoreThreshold;
        double sellThreshold = _config.SellScoreThreshold;

        // Apply regime threshold adjustments
        if (regime != null && regime.Regime != MarketRegime.Unknown)
        {
            buyThreshold *= regime.BuyThresholdMult;
            sellThreshold *= regime.SellThresholdMult;
        }

        // ══════════════════════════════════════════════════════
        //  P0 FIX: Softer no-consensus penalty
        //
        //  Old: flat 1.15× multiplier whenever brains disagree
        //       → effective threshold up to 38% above base
        //
        //  New: penalty scales with disagreement magnitude (0–20%),
        //       halved in strong regimes where the regime already
        //       tells us which brain is more trustworthy.
        // ══════════════════════════════════════════════════════

        if (!consensus)
        {
            double disagreementPenalty = 1.0 + (0.20 * (1.0 - directionalAlignment));

            // Reduce penalty when regime provides directional guidance
            if (regime != null && regime.Strength >= 0.5)
                disagreementPenalty = 1.0 + (disagreementPenalty - 1.0) * 0.5;

            buyThreshold *= disagreementPenalty;
            sellThreshold *= disagreementPenalty;
        }

        // ── Build decision ──
        var decision = new MetaDecision
        {
            FinalScore = weightedScore,
            Confidence = avgConfidence,
            Consensus = consensus,
            BrainCount = votes.Count,
            DirectionalAlignment = directionalAlignment,
        };

        // Build reasoning string
        var voteStrings = votes.Select(v =>
            $"{v.Name}={v.Score:F3}(conf={v.Confidence:F2},w={v.Weight:F2})");
        decision.Reasoning = $"[{string.Join(" | ", voteStrings)}] " +
            $"→ Final={weightedScore:F3} " +
            $"(consensus={consensus}, align={directionalAlignment:F2}, " +
            $"threshold={buyThreshold:F3})";

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

    // ── P0 FIX: Expose alignment for logging ──
    public double DirectionalAlignment { get; set; }
}