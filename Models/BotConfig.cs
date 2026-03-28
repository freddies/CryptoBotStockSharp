namespace CryptoBotStockSharp.Models;

public class BotConfig
{
    // ── Connection ──
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public bool UseTestnet { get; set; } = true;

    // ── Assets ──
    public string[] Assets { get; set; } = Array.Empty<string>();
    public int CandleTimeframeMinutes { get; set; } = 15;

    // ── Position Sizing ──
    public double PositionSizePercent { get; set; } = 40.0;
    public int MaxPositionsPerAsset { get; set; } = 1;
    public int MaxTotalPositions { get; set; } = 2;
    public double MinOrderValueUsd { get; set; } = 11.0;

    // ── Risk Management ──
    public double StopLossPercent { get; set; } = 2.5;
    public double TakeProfitPercent { get; set; } = 5.0;
    public double TrailingStopActivationPercent { get; set; } = 3.0;
    public double TrailingStopDistancePercent { get; set; } = 1.5;
    public double MaxDailyLossPercent { get; set; } = 5.0;
    public int CooldownAfterLossCandles { get; set; } = 3;
    public int MaxConsecutiveLosses { get; set; } = 3;

    // P2 Fix: Configurable fee rate (per side). Binance default = 0.1% = 0.001
    public double TradingFeeRate { get; set; } = 0.001;

    // P2 Fix: Minimum PnL% to allow signal-based exit
    public double MinExitPnlPercent { get; set; } = 0.3;

    // P2 Fix: Whether to reset cooldown/consecutive loss counters at midnight UTC.
    // false = safer: a losing streak near midnight still enforces cooldown across day boundary.
    // true  = original behavior: fresh start every day.
    public bool ResetCooldownOnNewDay { get; set; } = false;

    // ── RSI ──
    public int RsiPeriod { get; set; } = 14;
    public double RsiOversold { get; set; } = 30.0;
    public double RsiOverbought { get; set; } = 70.0;

    // ── Bollinger Bands ──
    public int BollingerPeriod { get; set; } = 20;
    public double BollingerStdDev { get; set; } = 2.0;

    // ── EMA ──
    public int EmaFastPeriod { get; set; } = 9;
    public int EmaSlowPeriod { get; set; } = 21;

    // ── MACD ──
    public int MacdFastPeriod { get; set; } = 12;
    public int MacdSlowPeriod { get; set; } = 26;
    public int MacdSignalPeriod { get; set; } = 9;

    // ── Signal Thresholds ──
    public double BuyScoreThreshold { get; set; } = 0.25;
    public double SellScoreThreshold { get; set; } = -0.25;

    // ── Indicator Weights ──
    public double WeightRsi { get; set; } = 0.30;
    public double WeightBollinger { get; set; } = 0.25;
    public double WeightEma { get; set; } = 0.25;
    public double WeightMacd { get; set; } = 0.20;

    // ── Logging ──
    public int LogIntervalCandles { get; set; } = 4;

    // ── Brain 2: StockSharp Indicators ──
    public int StochPeriod { get; set; } = 14;
    public int StochSmoothing { get; set; } = 3;
    public int AdxPeriod { get; set; } = 14;
    public int AtrPeriod { get; set; } = 14;

    // P2 Fix: Default changed from 14 → 28 to differentiate from Stochastic.
    // With both at 14, Williams %R ≈ inverted Stochastic, contributing redundant
    // information and giving oscillators ~50% of Brain2's vote.
    // At 28, W%R captures medium-term overbought/oversold vs Stoch's short-term.
    public int WilliamsRPeriod { get; set; } = 28;

    // ── Brain Weights (must sum to > 0) ──
    public double WeightBrainTechnical { get; set; } = 0.55;
    public double WeightBrainStockSharp { get; set; } = 0.45;

    // ── Market Regime Detection ──
    public bool UseRegimeDetection { get; set; } = true;
    public int RegimeLookbackCandles { get; set; } = 50;
    public int RegimeMinHistory { get; set; } = 20;

    // ── Future: LLM Brain ──
    public bool UseLlmBrain { get; set; } = false;
    public string LlmProvider { get; set; } = "anthropic";
    public string LlmApiKey { get; set; } = "";
    public string LlmModel { get; set; } = "claude-sonnet-4-20250514";
    public double WeightBrainLlm { get; set; } = 0.0;
}