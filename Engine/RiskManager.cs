using System.Text.Json;
using CryptoBotStockSharp.Models;

namespace CryptoBotStockSharp.Engine;

/// <summary>
/// Manages risk per trade and per day.
/// Enforces stop-loss, take-profit, trailing stops, and daily limits.
/// Persists open positions and daily state to disk for crash recovery.
///
/// THREAD SAFETY CONTRACT (P1 Fix):
///   All public methods must be called while the caller holds
///   TradingBot._lock, OR from a guaranteed single-threaded context
///   (e.g., during initialization before the message pump starts).
///   RiskManager itself does NOT perform internal locking on _positions
///   or counters — it relies on external synchronization.
///
/// P0 Fixes:
///   - Fee rate reads from _config.TradingFeeRate instead of hardcoded 0.001
///   - UpdateBalance no longer overwrites daily loss baseline
///   - OpenPosition uses validated TradePosition constructor
///   - LoadState validates deserialized positions before accepting them
///
/// P1 Fixes:
///   - CheckRestoredPositions() detects stale/underwater positions after crash recovery
///   - Thread safety contract documented
/// </summary>
public class RiskManager
{
    private readonly BotConfig _config;
    private decimal _dailyPnl;

    // ── P0 FIX: Separate daily baseline from live balance ──
    private decimal _dailyStartBalance;
    private decimal _currentBalance;

    private DateTime _currentDay;
    private int _consecutiveLosses;
    private int _cooldownRemaining;

    // Track all open positions
    private readonly Dictionary<string, TradePosition> _positions = new();

    // ── Persistence ──
    private readonly string? _stateFilePath;
    private readonly object _persistLock = new();

    public int OpenPositionCount => _positions.Count;
    public IReadOnlyDictionary<string, TradePosition> Positions => _positions;
    public decimal DailyPnl => _dailyPnl;

    public RiskManager(BotConfig config, decimal startingBalance, string? stateDirectory = null)
    {
        _config = config;
        _currentBalance = startingBalance;
        _dailyStartBalance = startingBalance;
        _currentDay = DateTime.UtcNow.Date;
        _dailyPnl = 0;
        _consecutiveLosses = 0;
        _cooldownRemaining = 0;

        if (stateDirectory != null)
        {
            Directory.CreateDirectory(stateDirectory);
            _stateFilePath = Path.Combine(stateDirectory, "risk_state.json");
            LoadState();
        }
    }

    // ══════════════════════════════════════════════════════════
    //  POSITION QUERIES
    // ══════════════════════════════════════════════════════════

    public bool HasPosition(string asset) => _positions.ContainsKey(asset);

    public TradePosition? GetPosition(string asset) =>
        _positions.TryGetValue(asset, out var pos) ? pos : null;

    // ══════════════════════════════════════════════════════════
    //  P1 FIX: STALE POSITION DETECTION
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Validates restored positions against current market prices.
    /// Call after historical candles are loaded and _lastPrices are populated.
    ///
    /// Returns a list of warnings. Does NOT close positions — the first
    /// live candle will trigger CheckPositionRisk which handles SL/TP.
    /// This method exists so the operator sees immediate warnings in the log
    /// rather than discovering problems minutes later when the next candle arrives.
    /// </summary>
    public List<RestoredPositionWarning> CheckRestoredPositions(
        Func<string, decimal?> priceProvider)
    {
        var warnings = new List<RestoredPositionWarning>();

        foreach (var (asset, pos) in _positions)
        {
            decimal? currentPrice = priceProvider(asset);
            if (currentPrice == null || currentPrice <= 0)
            {
                var age = DateTimeOffset.UtcNow - pos.EntryTime;
                warnings.Add(new RestoredPositionWarning
                {
                    Asset = asset,
                    Severity = PositionWarningSeverity.Warning,
                    Message = $"No current price available (entry ${pos.EntryPrice:F2}, " +
                              $"age={age.TotalHours:F0}h). Will check on first candle."
                });
                continue;
            }

            var price = currentPrice.Value;
            var posAge = DateTimeOffset.UtcNow - pos.EntryTime;
            double pnlPct = pos.GetUnrealizedPnlPercent(price);

            if (price <= pos.StopLoss)
            {
                warnings.Add(new RestoredPositionWarning
                {
                    Asset = asset,
                    Severity = PositionWarningSeverity.Critical,
                    Message = $"Price ${price:F2} BELOW stop loss ${pos.StopLoss:F2} " +
                              $"(entry ${pos.EntryPrice:F2}, PnL={pnlPct:F2}%, " +
                              $"age={posAge.TotalHours:F0}h). Will close on next candle."
                });
            }
            else if (price >= pos.TakeProfit)
            {
                warnings.Add(new RestoredPositionWarning
                {
                    Asset = asset,
                    Severity = PositionWarningSeverity.Info,
                    Message = $"Price ${price:F2} ABOVE take profit ${pos.TakeProfit:F2} " +
                              $"(entry ${pos.EntryPrice:F2}, PnL={pnlPct:F2}%, " +
                              $"age={posAge.TotalHours:F0}h). Will close on next candle."
                });
            }
            else if (pos.TrailingStopActive && price <= pos.TrailingStop)
            {
                warnings.Add(new RestoredPositionWarning
                {
                    Asset = asset,
                    Severity = PositionWarningSeverity.Critical,
                    Message = $"Price ${price:F2} BELOW trailing stop ${pos.TrailingStop:F2} " +
                              $"(high=${pos.HighestPriceSinceEntry:F2}, PnL={pnlPct:F2}%, " +
                              $"age={posAge.TotalHours:F0}h). Will close on next candle."
                });
            }
            else if (Math.Abs(pnlPct) > 5.0)
            {
                var severity = pnlPct < -3.0
                    ? PositionWarningSeverity.Warning
                    : PositionWarningSeverity.Info;

                warnings.Add(new RestoredPositionWarning
                {
                    Asset = asset,
                    Severity = severity,
                    Message = $"Large unrealized PnL: {pnlPct:F2}% " +
                              $"(entry ${pos.EntryPrice:F2}, now ${price:F2}, " +
                              $"age={posAge.TotalHours:F0}h)"
                });
            }
            else if (posAge.TotalHours > 48)
            {
                warnings.Add(new RestoredPositionWarning
                {
                    Asset = asset,
                    Severity = PositionWarningSeverity.Warning,
                    Message = $"Position is {posAge.TotalHours:F0}h old " +
                              $"(entry ${pos.EntryPrice:F2}, now ${price:F2}, " +
                              $"PnL={pnlPct:F2}%)"
                });
            }
        }

        return warnings;
    }

    // ══════════════════════════════════════════════════════════
    //  ENTRY VALIDATION
    // ══════════════════════════════════════════════════════════

    public (bool Allowed, string Reason) CanOpenPosition(string asset, decimal currentBalance)
    {
        ResetDailyIfNeeded();

        if (_cooldownRemaining > 0)
            return (false, $"Cooling down ({_cooldownRemaining} candles remaining)");

        if (_consecutiveLosses >= _config.MaxConsecutiveLosses)
            return (false, $"Max consecutive losses reached ({_consecutiveLosses})");

        decimal maxDailyLoss = _dailyStartBalance * (decimal)(_config.MaxDailyLossPercent / 100.0);
        if (_dailyPnl <= -maxDailyLoss)
            return (false, $"Daily loss limit hit (PnL={_dailyPnl:F2}, " +
                           $"limit=-${maxDailyLoss:F2} based on day-start ${_dailyStartBalance:F2})");

        if (_positions.ContainsKey(asset))
            return (false, "Already have open position");

        if (_positions.Count >= _config.MaxTotalPositions)
            return (false, $"Max total positions reached ({_positions.Count})");

        decimal positionValue = currentBalance * (decimal)(_config.PositionSizePercent / 100.0);
        if (positionValue < (decimal)_config.MinOrderValueUsd)
            return (false, $"Position size too small (${positionValue:F2} < ${_config.MinOrderValueUsd})");

        return (true, "OK");
    }

    // ══════════════════════════════════════════════════════════
    //  POSITION SIZING
    // ══════════════════════════════════════════════════════════

    public decimal CalculatePositionSize(decimal currentBalance, decimal assetPrice)
    {
        if (assetPrice <= 0) return 0;

        decimal usdToSpend = currentBalance * (decimal)(_config.PositionSizePercent / 100.0);
        decimal volume = usdToSpend / assetPrice;

        return Math.Round(volume, 6);
    }

    // ══════════════════════════════════════════════════════════
    //  POSITION OPEN / CLOSE
    // ══════════════════════════════════════════════════════════

    public TradePosition OpenPosition(string asset, decimal entryPrice, decimal volume)
    {
        decimal stopLoss = entryPrice * (1m - (decimal)(_config.StopLossPercent / 100.0));
        decimal takeProfit = entryPrice * (1m + (decimal)(_config.TakeProfitPercent / 100.0));

        var position = new TradePosition(asset, entryPrice, volume, stopLoss, takeProfit);

        _positions[asset] = position;
        SaveState();
        return position;
    }

    public decimal ClosePosition(string asset, decimal exitPrice)
    {
        if (!_positions.TryGetValue(asset, out var position))
            return 0;

        decimal pnl = position.GetUnrealizedPnl(exitPrice);

        decimal feeRate = (decimal)_config.TradingFeeRate;
        decimal entryFee = position.Volume * position.EntryPrice * feeRate;
        decimal exitFee = position.Volume * exitPrice * feeRate;
        decimal netPnl = pnl - entryFee - exitFee;

        _dailyPnl += netPnl;
        _positions.Remove(asset);

        if (netPnl < 0)
        {
            _consecutiveLosses++;
            if (_consecutiveLosses >= _config.MaxConsecutiveLosses)
            {
                _cooldownRemaining = _config.CooldownAfterLossCandles * 3;
            }
        }
        else
        {
            _consecutiveLosses = 0;
        }

        SaveState();
        return netPnl;
    }

    // ══════════════════════════════════════════════════════════
    //  POSITION RISK CHECKS
    // ══════════════════════════════════════════════════════════

    public (bool ShouldClose, string Reason) CheckPositionRisk(
        string asset, decimal currentPrice)
    {
        if (!_positions.TryGetValue(asset, out var pos))
            return (false, "");

        if (currentPrice <= pos.StopLoss)
            return (true, $"STOP LOSS hit at {currentPrice:F2} (SL={pos.StopLoss:F2})");

        if (currentPrice >= pos.TakeProfit)
            return (true, $"TAKE PROFIT hit at {currentPrice:F2} (TP={pos.TakeProfit:F2})");

        double pnlPercent = pos.GetUnrealizedPnlPercent(currentPrice);

        if (!pos.TrailingStopActive &&
            pnlPercent >= _config.TrailingStopActivationPercent)
        {
            pos.TrailingStopActive = true;
            pos.HighestPriceSinceEntry = currentPrice;
            pos.TrailingStop = currentPrice *
                (1m - (decimal)(_config.TrailingStopDistancePercent / 100.0));
        }

        if (pos.TrailingStopActive)
        {
            pos.UpdateTrailingStop(currentPrice, _config.TrailingStopDistancePercent);

            if (currentPrice <= pos.TrailingStop)
                return (true, $"TRAILING STOP hit at {currentPrice:F2} " +
                              $"(Trail={pos.TrailingStop:F2}, " +
                              $"High={pos.HighestPriceSinceEntry:F2})");
        }

        return (false, "");
    }

    // ══════════════════════════════════════════════════════════
    //  COOLDOWN & DAILY RESET
    // ══════════════════════════════════════════════════════════

    public void TickCooldown()
    {
        if (_cooldownRemaining > 0)
            _cooldownRemaining--;

        if (_cooldownRemaining == 0 && _consecutiveLosses >= _config.MaxConsecutiveLosses)
            _consecutiveLosses = 0;
    }

    public void UpdateBalance(decimal newBalance)
    {
        if (newBalance > 0)
            _currentBalance = newBalance;
    }

    private void ResetDailyIfNeeded()
    {
        if (DateTime.UtcNow.Date != _currentDay)
        {
            _currentDay = DateTime.UtcNow.Date;
            _dailyStartBalance = _currentBalance;
            _dailyPnl = 0;
            // P2 Fix: Configurable midnight reset behavior.
            // When ResetCooldownOnNewDay=false (default), a losing streak near
            // midnight still enforces cooldown across the day boundary.
            // When true, original behavior: fresh start every day.
            if (_config.ResetCooldownOnNewDay)
            {
                _consecutiveLosses = 0;
                _cooldownRemaining = 0;
            }
            SaveState();
        }
    }

    // ══════════════════════════════════════════════════════════
    //  STATE PERSISTENCE
    // ══════════════════════════════════════════════════════════

    private void SaveState()
    {
        if (_stateFilePath == null) return;

        lock (_persistLock)
        {
            try
            {
                var state = new RiskManagerState
                {
                    SavedAtUtc = DateTime.UtcNow,
                    DailyPnl = _dailyPnl,
                    CurrentBalance = _currentBalance,
                    DailyStartBalance = _dailyStartBalance,
                    CurrentDay = _currentDay,
                    ConsecutiveLosses = _consecutiveLosses,
                    CooldownRemaining = _cooldownRemaining,
                    Positions = _positions.Values.Select(p => new PersistedPosition
                    {
                        Asset = p.Asset,
                        EntryPrice = p.EntryPrice,
                        Volume = p.Volume,
                        StopLoss = p.StopLoss,
                        TakeProfit = p.TakeProfit,
                        TrailingStop = p.TrailingStop,
                        TrailingStopActive = p.TrailingStopActive,
                        HighestPriceSinceEntry = p.HighestPriceSinceEntry,
                        EntryTimeUnixMs = p.EntryTime.ToUnixTimeMilliseconds()
                    }).ToList()
                };

                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                var tmpPath = _stateFilePath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _stateFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ RiskManager state save failed: {ex.Message}");
            }
        }
    }

    private void LoadState()
    {
        if (_stateFilePath == null || !File.Exists(_stateFilePath)) return;

        lock (_persistLock)
        {
            try
            {
                var json = File.ReadAllText(_stateFilePath);
                var state = JsonSerializer.Deserialize<RiskManagerState>(json);

                if (state == null) return;

                if (state.CurrentDay.Date == DateTime.UtcNow.Date)
                {
                    _dailyPnl = state.DailyPnl;
                    _consecutiveLosses = state.ConsecutiveLosses;
                    _cooldownRemaining = state.CooldownRemaining;
                    _currentDay = state.CurrentDay;

                    if (state.DailyStartBalance > 0)
                        _dailyStartBalance = state.DailyStartBalance;
                    if (state.CurrentBalance > 0)
                        _currentBalance = state.CurrentBalance;
                }
                else
                {
                    if (state.CurrentBalance > 0)
                    {
                        _currentBalance = state.CurrentBalance;
                        _dailyStartBalance = state.CurrentBalance;
                    }
                }

                if (state.Positions != null)
                {
                    foreach (var pp in state.Positions)
                    {
                        if (string.IsNullOrWhiteSpace(pp.Asset) ||
                            pp.EntryPrice <= 0 || pp.Volume <= 0)
                        {
                            Console.WriteLine($"⚠️ Skipping invalid persisted position: " +
                                $"Asset={pp.Asset} Entry={pp.EntryPrice} Vol={pp.Volume}");
                            continue;
                        }

                        if (pp.StopLoss <= 0)
                            pp.StopLoss = pp.EntryPrice * (1m - (decimal)(_config.StopLossPercent / 100.0));
                        if (pp.TakeProfit <= 0)
                            pp.TakeProfit = pp.EntryPrice * (1m + (decimal)(_config.TakeProfitPercent / 100.0));

                        try
                        {
                            var pos = new TradePosition(
                                pp.Asset, pp.EntryPrice, pp.Volume,
                                pp.StopLoss, pp.TakeProfit)
                            {
                                TrailingStop = pp.TrailingStop,
                                TrailingStopActive = pp.TrailingStopActive,
                                HighestPriceSinceEntry = pp.HighestPriceSinceEntry > 0
                                    ? pp.HighestPriceSinceEntry
                                    : pp.EntryPrice,
                                EntryTime = DateTimeOffset.FromUnixTimeMilliseconds(pp.EntryTimeUnixMs)
                            };

                            _positions[pp.Asset] = pos;
                        }
                        catch (ArgumentException ex)
                        {
                            Console.WriteLine($"⚠️ Skipping invalid persisted position " +
                                $"{pp.Asset}: {ex.Message}");
                        }
                    }
                }

                Console.WriteLine($"✅ RiskManager state restored: " +
                    $"{_positions.Count} positions, " +
                    $"dailyPnL=${_dailyPnl:F4}, " +
                    $"dayStart=${_dailyStartBalance:F2}, " +
                    $"current=${_currentBalance:F2}, " +
                    $"saved at {state.SavedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ RiskManager state load failed: {ex.Message}");
            }
        }
    }
}

// ══════════════════════════════════════════════════════════
//  P1: STALE POSITION WARNING TYPES
// ══════════════════════════════════════════════════════════

public enum PositionWarningSeverity
{
    Info,
    Warning,
    Critical
}

public class RestoredPositionWarning
{
    public string Asset { get; set; } = "";
    public PositionWarningSeverity Severity { get; set; }
    public string Message { get; set; } = "";
}

// ══════════════════════════════════════════════════════════
//  PERSISTENCE MODELS
// ══════════════════════════════════════════════════════════

public class RiskManagerState
{
    public DateTime SavedAtUtc { get; set; }
    public decimal DailyPnl { get; set; }
    public decimal CurrentBalance { get; set; }
    public decimal DailyStartBalance { get; set; }

    [Obsolete("Use CurrentBalance + DailyStartBalance. Kept for old state file compat.")]
    public decimal StartingBalance
    {
        get => CurrentBalance;
        set
        {
            if (CurrentBalance == 0) CurrentBalance = value;
            if (DailyStartBalance == 0) DailyStartBalance = value;
        }
    }

    public DateTime CurrentDay { get; set; }
    public int ConsecutiveLosses { get; set; }
    public int CooldownRemaining { get; set; }
    public List<PersistedPosition> Positions { get; set; } = new();
}

public class PersistedPosition
{
    public string Asset { get; set; } = "";
    public decimal EntryPrice { get; set; }
    public decimal Volume { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal TrailingStop { get; set; }
    public bool TrailingStopActive { get; set; }
    public decimal HighestPriceSinceEntry { get; set; }
    public long EntryTimeUnixMs { get; set; }
}