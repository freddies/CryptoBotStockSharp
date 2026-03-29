using CryptoBotStockSharp.Indicators;
using CryptoBotStockSharp.Models;

namespace CryptoBotStockSharp.Engine;

/// <summary>
/// Manages trading for a single asset using multiple brains.
/// 
/// P2 Fixes:
///   - Hardcoded breakeven threshold replaced with config.MinExitPnlPercent
///   - _recentCandles exposed as RecentCandles property for future LLM brain
/// </summary>
public class AssetTrader
{
    private readonly string _asset;
    private readonly BotConfig _config;
    private readonly RiskManager _riskManager;

    // ── Brain 1: Custom technical indicators ──
    private readonly TechnicalIndicators _indicators;
    private readonly SignalEngine _signalEngine;

    // ── Brain 2: StockSharp-style indicators ──
    private readonly StockSharpBrain _brain2;

    // ── Market Regime Detector ──
    private readonly MarketRegimeDetector _regimeDetector;
    private RegimeInfo? _currentRegime;

    // ── Meta-Decision Engine ──
    private readonly MetaDecisionEngine _metaEngine;

    private int _candleCount;
    private decimal _lastPrice;
    private bool _isLive;

    // ── Deduplication ──
    private DateTimeOffset _lastProcessedCandleTime = DateTimeOffset.MinValue;
    private readonly object _candleLock = new();

    // ── Candle history for LLM brain context ──
    private readonly List<CandleRecord> _recentCandles = new();
    private readonly object _candleHistoryLock = new();  // FIX: dedicated lock
    private const int MaxCandleHistory = 50;

    // ── Balance provider callback ──
    public Func<decimal>? BalanceProvider { get; set; }

    // FIX: Thread-safe read access
    public IReadOnlyList<CandleRecord> RecentCandles
    {
        get
        {
            lock (_candleHistoryLock)
            {
                return _recentCandles.ToList();
            }
        }
    }
    // Callbacks
    public event Action<string, SignalType, decimal, decimal>? OnTradeSignal;
    public event Action<string>? OnLog;

    public string Asset => _asset;
    public decimal LastPrice => _lastPrice;
    public bool IsWarmedUp => _indicators.AllReady && _brain2.IsReady;
    public StockSharpBrain Brain2 => _brain2;

    public AssetTrader(
        string asset,
        BotConfig config,
        RiskManager riskManager)
    {
        _asset = asset;
        _config = config;
        _riskManager = riskManager;
        _isLive = false;

        _indicators = new TechnicalIndicators(
            rsiPeriod: config.RsiPeriod,
            bollingerPeriod: config.BollingerPeriod,
            bollingerStdDev: config.BollingerStdDev,
            emaFastPeriod: config.EmaFastPeriod,
            emaSlowPeriod: config.EmaSlowPeriod,
            macdFastPeriod: config.MacdFastPeriod,
            macdSlowPeriod: config.MacdSlowPeriod,
            macdSignalPeriod: config.MacdSignalPeriod
        );

        _signalEngine = new SignalEngine(config);

        _brain2 = new StockSharpBrain(
            stochPeriod: config.StochPeriod,
            stochSmoothing: config.StochSmoothing,
            adxPeriod: config.AdxPeriod,
            atrPeriod: config.AtrPeriod,
            williamsRPeriod: config.WilliamsRPeriod
        );

        _regimeDetector = new MarketRegimeDetector(
            lookback: config.RegimeLookbackCandles,
            minHistory: config.RegimeMinHistory
        );

        _metaEngine = new MetaDecisionEngine(config);
    }

    public void GoLive()
    {
        _isLive = true;
        Log($"🟢 [{_asset}] Now LIVE — signals will trigger real orders");
        Log($"🧠 [{_asset}] Brain1 ready: {_indicators.AllReady} | " +
            $"Brain2 ready: {_brain2.IsReady}");

        if (_brain2.IsReady)
            Log($"🧠 [{_asset}] Brain2 state: {_brain2.GetSummary()}");

        if (_currentRegime != null && _currentRegime.Regime != MarketRegime.Unknown)
            Log($"🌍 [{_asset}] Regime: {_currentRegime.Summary}");
    }

    public void ProcessCandle(decimal open, decimal high, decimal low,
                               decimal close, decimal volume, DateTimeOffset time)
    {
        lock (_candleLock)
        {
            if (time <= _lastProcessedCandleTime)
            {
                Log($"⏭️ [{_asset}] Skipping duplicate candle at {time:yyyy-MM-dd HH:mm}");
                return;
            }
            _lastProcessedCandleTime = time;
        }

        _candleCount++;
        _lastPrice = close;

        // FIX: Protect candle history with dedicated lock
        lock (_candleHistoryLock)
        {
            _recentCandles.Add(new CandleRecord
            {
                Time = time, Open = open, High = high,
                Low = low, Close = close, Volume = volume
            });
            if (_recentCandles.Count > MaxCandleHistory)
                _recentCandles.RemoveAt(0);
        }

        // ── Update both brains ──
        _indicators.Update(close);
        _brain2.Update(open, high, low, close, volume);

        // ── Update regime detector ──
        if (_indicators.AllReady && _brain2.IsReady && _config.UseRegimeDetection)
        {
            double atrPercent = close > 0
                ? (double)_brain2.Atr / (double)close * 100.0
                : 0;

            _currentRegime = _regimeDetector.Update(
                bbWidth: (double)_indicators.BollingerWidth,
                atrPercent: atrPercent,
                rawAdx: (double)_brain2.Adx,
                plusDi: (double)_brain2.PlusDi,
                minusDi: (double)_brain2.MinusDi,
                rsi: (double)_indicators.Rsi,
                emaFastAboveSlow: _indicators.EmaFastAboveSlow
            );
        }

        _riskManager.TickCooldown();

        // ── Periodic logging ──
        if (_candleCount % _config.LogIntervalCandles == 0)
        {
            if (_indicators.AllReady)
                Log($"📊 [{_asset}] B1: {_indicators.GetSummary(close)}");
            if (_brain2.IsReady)
                Log($"📊 [{_asset}] B2: {_brain2.GetSummary()}");
            if (_currentRegime != null && _currentRegime.Regime != MarketRegime.Unknown)
                Log($"🌍 [{_asset}] Regime: {_currentRegime.Summary}");
        }

        if (!_indicators.AllReady)
        {
            if (_candleCount % 5 == 0)
                Log($"⏳ [{_asset}] Warming up... {_candleCount} candles " +
                    $"(B1={_indicators.CandleCount}, B2={_brain2.CandleCount})");
            return;
        }

        if (!_isLive)
        {
            var signal = _signalEngine.Analyze(_indicators, close, time);
            var b2 = _brain2.IsReady ? _brain2.Analyze(close) : null;
            var meta = _metaEngine.Decide(signal, b2, _currentRegime);
            Log($"🔍 [{_asset}] #{_candleCount} [HISTORICAL] {meta.Action} " +
                $"Score={meta.FinalScore:F3} {meta.Reasoning}");
            return;
        }

        if (_riskManager.HasPosition(_asset))
        {
            HandleExistingPosition(close, time);
            return;
        }

        HandleNewSignal(close, time);
    }

    // ══════════════════════════════════════════════════════════
    //  POSITION MANAGEMENT
    // ══════════════════════════════════════════════════════════

    private void HandleExistingPosition(decimal currentPrice, DateTimeOffset candleTime)
    {
        var (shouldClose, reason) = _riskManager.CheckPositionRisk(_asset, currentPrice);

        if (shouldClose)
        {
            var pos = _riskManager.GetPosition(_asset)!;
            Log($"🔔 [{_asset}] Closing position: {reason}");
            OnTradeSignal?.Invoke(_asset, SignalType.Sell, currentPrice, pos.Volume);
            return;
        }

        var signal = _signalEngine.Analyze(_indicators, currentPrice, candleTime);
        var b2Signal = _brain2.IsReady ? _brain2.Analyze(currentPrice) : null;
        var meta = _metaEngine.Decide(signal, b2Signal, _currentRegime);

        if (meta.Action == SignalType.Sell)
        {
            var pos = _riskManager.GetPosition(_asset)!;
            double pnlPct = pos.GetUnrealizedPnlPercent(currentPrice);

            // P2 Fix: Use config instead of hardcoded 0.3
            if (pnlPct > _config.MinExitPnlPercent)
            {
                Log($"📉 [{_asset}] Multi-brain SELL signal: {meta.Reasoning}");
                OnTradeSignal?.Invoke(_asset, SignalType.Sell, currentPrice, pos.Volume);
            }
            else
            {
                Log($"🔍 [{_asset}] Sell signal but PnL too low " +
                    $"({pnlPct:F2}% < {_config.MinExitPnlPercent}%), holding");
            }
        }
    }

    private void HandleNewSignal(decimal currentPrice, DateTimeOffset candleTime)
    {
        var technicalSignal = _signalEngine.Analyze(_indicators, currentPrice, candleTime);
        var brain2Signal = _brain2.IsReady ? _brain2.Analyze(currentPrice) : null;
        var meta = _metaEngine.Decide(technicalSignal, brain2Signal, _currentRegime);

        Log($"🔍 [{_asset}] #{_candleCount} {meta.Action} " +
            $"Score={meta.FinalScore:F3} " +
            $"(consensus={meta.Consensus}, brains={meta.BrainCount}) " +
            $"{meta.Reasoning}");

        if (meta.Action != SignalType.Buy)
            return;

        decimal currentBalance = BalanceProvider?.Invoke() ?? 0m;
        if (currentBalance <= 0)
        {
            Log($"⛔ [{_asset}] Trade blocked: balance unavailable or zero");
            return;
        }

        var (canOpen, denyReason) = _riskManager.CanOpenPosition(_asset, currentBalance);
        if (!canOpen)
        {
            Log($"⛔ [{_asset}] Trade blocked: {denyReason}");
            return;
        }

        Log($"🚀 [{_asset}] MULTI-BRAIN BUY at {currentPrice:F2} " +
            $"(Score={meta.FinalScore:F3}, Conf={meta.Confidence:F2}, " +
            $"Trend={meta.TrendDirection}, Regime={meta.MarketRegime})");

        OnTradeSignal?.Invoke(_asset, SignalType.Buy, currentPrice, 0);
    }

    private void Log(string message) => OnLog?.Invoke(message);
}

public class CandleRecord
{
    public DateTimeOffset Time { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
}