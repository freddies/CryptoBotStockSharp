using System.Collections.Concurrent;
using System.Security;
using CryptoBotStockSharp.Models;
using StockSharp.Binance;
using StockSharp.Messages;
using Ecng.Common;

using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CryptoBotStockSharp.Engine;

/// <summary>
/// Main trading bot engine.
///
/// P0 Fixes:
///   - StopAsync: positions closed BEFORE CTS cancellation
///   - StopAsync: atomic guard prevents double-shutdown race
///   - HandleExecution: rejects WS fills without exchange OrderId
///   - CloseAllPositions: now async, awaits REST order completion
///
/// P1 Fixes:
///   - _lastPrices: ConcurrentDictionary for thread-safe writes from adapter thread
///   - HandleCandle: TOCTOU fixed by checking _tradersInitialized inside _lock
///   - _bufferLock eliminated: _candleBuffer now protected by _lock
///   - ThrottledRestCallAsync: pre-call delay replaces fire-and-forget release
///   - Stale position warnings logged after historical candle preload
///
/// P2: REST rate limiting, _processedOrderIds trim, configurable fees
/// </summary>
public class TradingBot : IDisposable
{
    private readonly BotConfig _config;
    private BinanceMessageAdapter _adapter = null!;
    private RiskManager _riskManager = null!;

    // Asset traders
    private readonly Dictionary<string, AssetTrader> _traders = new();
    private volatile bool _tradersInitialized;

    // Security info cache
    private readonly Dictionary<string, SecurityId> _securityIds = new();

    // P1 FIX: ConcurrentDictionary for thread-safe price updates.
    // HandleLevel1 writes from the adapter message thread without holding _lock.
    // Previously used Dictionary<> which is not thread-safe for concurrent read/write.
    private readonly ConcurrentDictionary<string, decimal> _lastPrices = new();

    // Portfolio
    private decimal _currentBalance;
    private decimal _initialBalance;
    private string _portfolioName = "Binance";

    // State
    private bool _isRunning;
    private bool _isConnected;
    private readonly object _lock = new();
    private int _totalTrades;
    private int _winningTrades;
    private int _losingTrades;
    private decimal _totalRealizedPnl;
    private long _transactionId;

    // ── P0: Atomic shutdown guard ──
    private int _stopRequested;

    // ── P0: Dedup order fills ──
    private readonly Dictionary<long, DateTime> _processedOrderIds = new();
    private readonly object _orderIdLock = new();
    private DateTime _lastOrderIdTrim = DateTime.UtcNow;
    private const int MaxProcessedOrderIds = 1000;

    // ── P1 FIX: Candle buffer protected by _lock (was separate _bufferLock) ──
    // Eliminates TOCTOU race where _tradersInitialized flips between
    // the volatile read and the buffer add, losing the candle.
    private readonly List<BufferedCandle> _candleBuffer = new();

    // ── P1: Exchange info for LOT_SIZE ──
    private readonly Dictionary<string, SymbolFilter> _symbolFilters = new();
    private bool _exchangeInfoLoaded;

    // ── P1 FIX: REST rate limiting with pre-call delay ──
    // Old approach: fire-and-forget Task.Run to release semaphore after delay.
    // Problem: the background task could outlive Dispose(), throwing
    // ObjectDisposedException on _restThrottle.Release().
    // New approach: delay BEFORE the call, release immediately in finally.
    private readonly SemaphoreSlim _restThrottle = new(1, 1);
    private const int RestDelayMs = 100;
    private DateTime _lastRestCallTime = DateTime.MinValue;

    // Cancellation
    private readonly CancellationTokenSource _cts = new();

    // Logging
    private readonly string _logFile;
    private readonly object _logLock = new();

    // Persistence
    private readonly string _stateDirectory;

    public TradingBot(BotConfig config)
    {
        _config = config;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _logFile = Path.Combine(baseDir, "logs",
            $"bot_log_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
        _stateDirectory = Path.Combine(baseDir, "state");

        Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
        Directory.CreateDirectory(_stateDirectory);
    }

    private long NextTransactionId() => Interlocked.Increment(ref _transactionId);

    // ══════════════════════════════════════════════════════════
    //  START / STOP
    // ══════════════════════════════════════════════════════════

    public async Task StartAsync()
    {
        Log("════════════════════════════════════════════════════════");
        Log("  CryptoBotStockSharp — Starting Up");
        Log($"  Mode: {(_config.UseTestnet ? "🧪 TESTNET" : "🔴 LIVE TRADING")}");
        Log($"  Assets: {string.Join(", ", _config.Assets)}");
        Log($"  Timeframe: {_config.CandleTimeframeMinutes}min candles");
        Log($"  Stop Loss: {_config.StopLossPercent}% | Take Profit: {_config.TakeProfitPercent}%");
        Log($"  Trailing Stop: {_config.TrailingStopActivationPercent}% activation, " +
            $"{_config.TrailingStopDistancePercent}% distance");
        Log($"  Max Daily Loss: {_config.MaxDailyLossPercent}%");
        Log($"  Fee Rate: {_config.TradingFeeRate * 100:F2}% per side");
        Log($"  Min Exit PnL: {_config.MinExitPnlPercent}%");
        Log($"  State dir: {_stateDirectory}");
        Log($"  Log file: {_logFile}");
        Log("════════════════════════════════════════════════════════");

        if (!_config.UseTestnet)
        {
            Log("⚠️  WARNING: LIVE TRADING MODE — Real money at risk!");
            Log("⚠️  Press Ctrl+C to abort within 10 seconds...");
            try { await Task.Delay(10000, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }

        SetupAdapter();

        Log("🔌 Connecting to Binance...");
        Connect();

        _isRunning = true;
        _ = Task.Run(StatusReportLoop);
    }

    public async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _stopRequested, 1, 0) != 0)
        {
            Log("ℹ️ StopAsync already in progress, skipping duplicate call.");
            return;
        }

        Log("🛑 Shutting down bot...");
        _isRunning = false;

        await CloseAllPositionsAsync();

        try { _cts.Cancel(); } catch { }

        if (_isConnected)
        {
            try { _adapter.SendInMessage(new DisconnectMessage()); }
            catch { }
        }

        PrintFinalReport();
        Log("👋 Bot stopped.");
        await Task.Delay(2000);
    }

    // ══════════════════════════════════════════════════════════
    //  ADAPTER SETUP
    // ══════════════════════════════════════════════════════════

    private void SetupAdapter()
    {
        _adapter = new BinanceMessageAdapter(new IncrementalIdGenerator())
        {
            Key = ToSecureString(_config.ApiKey),
            Secret = ToSecureString(_config.ApiSecret),
        };

        _adapter.IsDemo = _config.UseTestnet;
        _adapter.NewOutMessage += OnMessage;
    }

    private void Connect()
    {
        _adapter.SendInMessage(new ConnectMessage());
    }

    // ══════════════════════════════════════════════════════════
    //  MESSAGE HANDLER
    // ══════════════════════════════════════════════════════════

    private void OnMessage(Message message)
    {
        try
        {
            if (message.Type != MessageTypes.Time)
                Log($"📨 MSG: {message.Type} | {message}");

            switch (message.Type)
            {
                case MessageTypes.Connect:
                    HandleConnect((ConnectMessage)message);
                    break;
                case MessageTypes.Disconnect:
                    HandleDisconnect((DisconnectMessage)message);
                    break;
                case MessageTypes.Security:
                    HandleSecurity((SecurityMessage)message);
                    break;
                case MessageTypes.Level1Change:
                    HandleLevel1((Level1ChangeMessage)message);
                    break;
                case MessageTypes.CandleTimeFrame:
                    HandleCandle((TimeFrameCandleMessage)message);
                    break;
                case MessageTypes.Portfolio:
                case MessageTypes.PortfolioChange:
                    HandlePortfolio(message);
                    break;
                case MessageTypes.Position:
                case MessageTypes.PositionChange:
                    HandlePosition(message);
                    break;
                case MessageTypes.Execution:
                    HandleExecution((ExecutionMessage)message);
                    break;
                case MessageTypes.Error:
                    var errorMsg = (ErrorMessage)message;
                    Log($"⚠️ Error: {errorMsg.Error?.Message}");
                    break;
                case MessageTypes.SubscriptionResponse:
                    var subResp = (SubscriptionResponseMessage)message;
                    if (subResp.Error != null)
                        Log($"⚠️ Subscription FAILED (txId={subResp.OriginalTransactionId}): {subResp.Error.Message}");
                    else
                        Log($"✅ Subscription OK (txId={subResp.OriginalTransactionId})");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"❌ Message handler error [{message.Type}]: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  CONNECTION
    // ══════════════════════════════════════════════════════════

    private void HandleConnect(ConnectMessage msg)
    {
        if (msg.Error != null)
        {
            Log($"❌ Connection failed: {msg.Error.Message}");
            return;
        }

        _isConnected = true;
        Log("✅ Connected to Binance!");

        var boardCode = _adapter.AssociatedBoards.FirstOrDefault() ?? "BNB";
        Log($"🏷️ Using board: {boardCode}");

        _ = Task.Run(async () =>
        {
            try
            {
                var usdt = await ThrottledRestCallAsync(() => FetchUsdtBalanceAsync());
                if (!usdt.HasValue || usdt.Value <= 0)
                {
                    Log("⚠️ REST balance fetch returned null/0. Traders not started.");
                    return;
                }

                await ThrottledRestCallAsync(LoadExchangeInfoAsync);

                lock (_lock)
                {
                    _currentBalance = usdt.Value;
                    if (_initialBalance == 0)
                        _initialBalance = _currentBalance;

                    Log($"💵 USDT Balance (REST): ${_currentBalance:F2}");

                    _riskManager ??= new RiskManager(_config, _currentBalance, _stateDirectory);
                    _riskManager.UpdateBalance(_currentBalance);

                    foreach (var pos in _riskManager.Positions)
                        Log($"🔄 Restored position: {pos.Value}");

                    InitializeTraders();
                }

                SubscribeMarketData();
                ReplayBufferedCandles();

                _ = Task.Run(CandlePollingLoop);
            }
            catch (Exception ex)
            {
                Log($"⚠️ Initialization failed: {ex.Message}");
            }
        });

        _adapter.SendInMessage(new PortfolioLookupMessage
        {
            TransactionId = NextTransactionId(),
        });
    }

    private void SubscribeMarketData()
    {
        foreach (var asset in _config.Assets.Distinct())
        {
            var secId = new SecurityId
            {
                SecurityCode = asset,
                BoardCode = BoardCodes.Binance,
            };

            _adapter.SendInMessage(new MarketDataMessage
            {
                SecurityId = secId,
                DataType2 = DataType.Level1,
                IsSubscribe = true,
                TransactionId = NextTransactionId(),
            });

            _adapter.SendInMessage(new MarketDataMessage
            {
                SecurityId = secId,
                DataType2 = DataType.Ticks,
                IsSubscribe = true,
                TransactionId = NextTransactionId(),
            });

            _adapter.SendInMessage(new MarketDataMessage
            {
                SecurityId = secId,
                DataType2 = DataType.TimeFrame(TimeSpan.FromMinutes(_config.CandleTimeframeMinutes)),
                IsSubscribe = true,
                TransactionId = NextTransactionId(),
            });

            _securityIds[asset] = secId;
            Log($"📡 Subscribed to all data for {asset}");
        }
    }

    /// <summary>
    /// P1 FIX: Uses _lock instead of separate _bufferLock.
    /// Consistent with HandleCandle which now also uses _lock for buffering.
    /// </summary>
    private void ReplayBufferedCandles()
    {
        List<BufferedCandle> toReplay;
        lock (_lock)
        {
            toReplay = new List<BufferedCandle>(_candleBuffer);
            _candleBuffer.Clear();
        }

        if (toReplay.Count > 0)
        {
            Log($"🔄 Replaying {toReplay.Count} buffered candles...");
            lock (_lock)
            {
                foreach (var bc in toReplay)
                {
                    var asset = FindAsset(bc.SecurityCode);
                    if (asset != null && _traders.TryGetValue(asset, out var trader))
                    {
                        trader.ProcessCandle(bc.Open, bc.High, bc.Low,
                            bc.Close, bc.Volume, bc.OpenTime);
                        _lastPrices[asset] = bc.Close;
                    }
                }
            }
        }
    }

    private void HandleDisconnect(DisconnectMessage msg)
    {
        _isConnected = false;
        Log("❌ Disconnected from Binance.");

        if (_isRunning)
        {
            Log("🔄 Attempting reconnect in 10 seconds...");
            Task.Delay(10000).ContinueWith(_ =>
            {
                if (_isRunning) Connect();
            });
        }
    }

    // ══════════════════════════════════════════════════════════
    //  SECURITY / LEVEL1 / CANDLE
    // ══════════════════════════════════════════════════════════

    private void HandleSecurity(SecurityMessage msg)
    {
        var code = msg.SecurityId.SecurityCode ?? "";
        var asset = FindAsset(code);
        if (asset != null)
            Log($"📊 Security info: {code}");
    }

    /// <summary>
    /// P1 FIX: _lastPrices is now ConcurrentDictionary, so this is safe
    /// without holding _lock. Previously used Dictionary which could
    /// corrupt its internal state on concurrent read/write.
    /// </summary>
    private void HandleLevel1(Level1ChangeMessage msg)
    {
        var code = msg.SecurityId.SecurityCode ?? "";
        var asset = FindAsset(code);
        if (asset == null) return;

        var lastPrice = msg.TryGetDecimal(Level1Fields.LastTradePrice);
        var bestBid = msg.TryGetDecimal(Level1Fields.BestBidPrice);
        var bestAsk = msg.TryGetDecimal(Level1Fields.BestAskPrice);

        decimal price = lastPrice ?? bestAsk ?? bestBid ?? 0;
        if (price > 0)
            _lastPrices[asset] = price;
    }

    /// <summary>
    /// P1 FIX: TOCTOU race eliminated.
    ///
    /// Old code:
    ///   if (!_tradersInitialized)          // volatile read, NO lock
    ///       lock (_bufferLock) { buffer }  // separate lock
    ///   else
    ///       lock (_lock) { process }       // main lock
    ///
    /// Race window: _tradersInitialized flips between the volatile read
    /// and the lock acquisition. The candle is neither buffered nor processed.
    ///
    /// New code: everything under _lock. The check + buffer/process is atomic.
    /// _bufferLock is eliminated entirely.
    /// </summary>
    private void HandleCandle(TimeFrameCandleMessage candle)
    {
        Log($"🕯️ Candle [{candle.SecurityId.SecurityCode}] state={candle.State} " +
            $"open={candle.OpenTime:HH:mm} O={candle.OpenPrice} H={candle.HighPrice} " +
            $"L={candle.LowPrice} C={candle.ClosePrice}");

        if (candle.State != CandleStates.Finished)
            return;

        var code = candle.SecurityId.SecurityCode ?? "";

        // P1 FIX: Single lock for both the initialized check AND the
        // buffer/process action. No window for _tradersInitialized to
        // change between the check and the action.
        lock (_lock)
        {
            if (!_tradersInitialized)
            {
                _candleBuffer.Add(new BufferedCandle
                {
                    SecurityCode = code,
                    Open = candle.OpenPrice,
                    High = candle.HighPrice,
                    Low = candle.LowPrice,
                    Close = candle.ClosePrice,
                    Volume = candle.TotalVolume,
                    OpenTime = candle.OpenTime
                });
                Log($"   📦 Buffered candle (traders not ready)");
                return;
            }

            var asset = FindAsset(code);
            if (asset != null && _traders.TryGetValue(asset, out var trader))
            {
                trader.ProcessCandle(
                    candle.OpenPrice, candle.HighPrice,
                    candle.LowPrice, candle.ClosePrice,
                    candle.TotalVolume, candle.OpenTime);
                _lastPrices[asset] = candle.ClosePrice;
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  PORTFOLIO / POSITION
    // ══════════════════════════════════════════════════════════

    private void HandlePortfolio(Message msg)
    {
        if (msg is PortfolioMessage pfMsg)
        {
            _portfolioName = pfMsg.PortfolioName ?? "Binance";
            Log($"💰 Portfolio: {_portfolioName}");
        }
    }

    private void HandlePosition(Message msg)
    {
        if (msg is PositionChangeMessage posMsg)
        {
            var code = posMsg.SecurityId.SecurityCode ?? "";
            var currentValue = posMsg.TryGetDecimal(PositionChangeTypes.CurrentValue);

            if (code.Contains("USDT", StringComparison.OrdinalIgnoreCase) && currentValue.HasValue)
            {
                _currentBalance = currentValue.Value;
                if (_initialBalance == 0)
                    _initialBalance = _currentBalance;

                Log($"💵 USDT Balance: ${_currentBalance:F2}");

                if (_riskManager == null)
                {
                    _riskManager = new RiskManager(_config, _currentBalance, _stateDirectory);
                    Log($"🛡️ Risk manager initialized with ${_currentBalance:F2}");
                }
                else
                {
                    _riskManager.UpdateBalance(_currentBalance);
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  EXECUTION
    // ══════════════════════════════════════════════════════════

    private void HandleExecution(ExecutionMessage msg)
    {
        if (msg.HasOrderInfo)
        {
            Log($"📋 Order #{msg.TransactionId} [{msg.SecurityId.SecurityCode}] " +
                $"{msg.Side} {msg.OrderVolume} → {msg.OrderState}");
        }

        if (msg.HasTradeInfo && msg.TradePrice > 0 && msg.TradeVolume > 0)
        {
            long? exchangeOrderId = msg.OrderId;
            if (exchangeOrderId == null || exchangeOrderId == 0)
            {
                Log($"⏭️ Ignoring WS fill without exchange OrderId " +
                    $"(txId={msg.TransactionId}, " +
                    $"price={msg.TradePrice}, vol={msg.TradeVolume}). " +
                    $"REST response will handle this fill.");
                return;
            }

            lock (_orderIdLock)
            {
                if (_processedOrderIds.ContainsKey(exchangeOrderId.Value))
                {
                    Log($"⏭️ Skipping duplicate fill #{exchangeOrderId}");
                    return;
                }
                _processedOrderIds[exchangeOrderId.Value] = DateTime.UtcNow;
            }

            Log($"✅ TRADE [WS]: {msg.Side} {msg.TradeVolume} " +
                $"{msg.SecurityId.SecurityCode} @ {msg.TradePrice}");

            lock (_lock)
            {
                var code = msg.SecurityId.SecurityCode ?? "";
                var asset = FindAsset(code);
                if (asset == null) return;

                ProcessFill(asset, msg.Side == Sides.Buy ? "BUY" : "SELL",
                    msg.TradePrice.Value, msg.TradeVolume.Value);
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  CENTRALIZED FILL PROCESSING
    // ══════════════════════════════════════════════════════════

    private void ProcessFill(string asset, string side, decimal price, decimal quantity)
    {
        if (side == "BUY")
        {
            var pos = _riskManager.OpenPosition(asset, price, quantity);
            Log($"📈 Position opened: {pos}");
            RefreshBalanceAsync();
        }
        else if (side == "SELL")
        {
            decimal pnl = _riskManager.ClosePosition(asset, price);
            _totalRealizedPnl += pnl;
            _totalTrades++;

            if (pnl >= 0) _winningTrades++;
            else _losingTrades++;

            Log($"📉 Position closed. PnL: ${pnl:F4} | " +
                $"Total: ${_totalRealizedPnl:F4} | WR: {GetWinRate():F1}%");

            RefreshBalanceAsync();
        }

        TrimProcessedOrderIds();
    }

    private void RefreshBalanceAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var bal = await ThrottledRestCallAsync(() => FetchUsdtBalanceAsync());
                if (bal.HasValue && bal.Value > 0)
                {
                    lock (_lock)
                    {
                        _currentBalance = bal.Value;
                        _riskManager?.UpdateBalance(_currentBalance);
                    }
                    Log($"💵 Updated balance: ${bal.Value:F2}");
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Balance refresh failed: {ex.Message}");
            }
        });
    }

    private void TrimProcessedOrderIds()
    {
        lock (_orderIdLock)
        {
            if (_processedOrderIds.Count <= MaxProcessedOrderIds)
                return;

            var cutoff = DateTime.UtcNow.AddHours(-1);
            var stale = _processedOrderIds
                .Where(kv => kv.Value < cutoff)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var id in stale)
                _processedOrderIds.Remove(id);

            if (stale.Count > 0)
                Log($"🧹 Trimmed {stale.Count} stale order IDs " +
                    $"({_processedOrderIds.Count} remaining)");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  TRADER INITIALIZATION
    // ══════════════════════════════════════════════════════════

    private void InitializeTraders()
    {
        if (_riskManager == null)
        {
            Log("❌ Risk manager not initialized yet.");
            return;
        }

        foreach (var asset in _config.Assets.Distinct())
        {
            if (_traders.ContainsKey(asset)) continue;

            var trader = new AssetTrader(asset, _config, _riskManager);
            trader.OnLog += (msg) => Log(msg);
            trader.OnTradeSignal += OnTradeSignalReceived;
            trader.BalanceProvider = () =>
            {
                lock (_lock) { return _currentBalance; }
            };

            _traders[asset] = trader;
            Log($"🤖 Trader initialized for {asset}");
        }

        _tradersInitialized = true;

        Log("══════════════════════════════════════════════════");
        Log("  🟢 BOT IS ACTIVE — Waiting for candles...");
        Log("══════════════════════════════════════════════════");
    }

    // ══════════════════════════════════════════════════════════
    //  TRADE EXECUTION
    // ══════════════════════════════════════════════════════════

    private void OnTradeSignalReceived(string asset, SignalType signal,
                                        decimal price, decimal volume)
    {
        lock (_lock)
        {
            try
            {
                if (!_securityIds.TryGetValue(asset, out var secId))
                {
                    Log($"⚠️ Security not found for {asset}");
                    return;
                }

                if (signal == SignalType.Buy)
                    ExecuteBuy(asset, secId, price);
                else if (signal == SignalType.Sell)
                    ExecuteSell(asset, secId, price, volume);
            }
            catch (Exception ex)
            {
                Log($"❌ Trade execution error [{asset}]: {ex.Message}");
            }
        }
    }

    private void ExecuteBuy(string asset, SecurityId secId, decimal currentPrice)
    {
        var (canOpen, reason) = _riskManager.CanOpenPosition(asset, _currentBalance);
        if (!canOpen)
        {
            Log($"⛔ [{asset}] Buy blocked: {reason}");
            return;
        }

        decimal positionVolume = _riskManager.CalculatePositionSize(
            _currentBalance, currentPrice);

        decimal notional = positionVolume * currentPrice;
        if (notional < (decimal)_config.MinOrderValueUsd)
        {
            Log($"⛔ [{asset}] Order too small: ${notional:F2}");
            return;
        }

        var key = asset.ToUpperInvariant();
        if (_symbolFilters.TryGetValue(key, out var filter) && filter.MinNotional > 0)
        {
            if (notional < filter.MinNotional)
            {
                Log($"⛔ [{asset}] Below exchange minNotional: " +
                    $"${notional:F2} < ${filter.MinNotional:F2}");
                return;
            }
        }

        positionVolume = RoundVolume(asset, positionVolume);
        if (positionVolume <= 0) return;

        Log($"🟢 [{asset}] BUY: {positionVolume} @ ~${currentPrice:F2} " +
            $"(${positionVolume * currentPrice:F2})");

        _ = Task.Run(() => ThrottledRestCallAsync(
            () => ExecuteOrderViaRestAsync(asset, "BUY", positionVolume)));
    }

    private void ExecuteSell(string asset, SecurityId secId,
                            decimal currentPrice, decimal volume)
    {
        var position = _riskManager.GetPosition(asset);
        decimal sellVolume = volume > 0 ? volume : (position?.Volume ?? 0);

        if (sellVolume <= 0)
        {
            Log($"⚠️ [{asset}] No volume to sell");
            return;
        }

        sellVolume = RoundVolume(asset, sellVolume);
        if (sellVolume <= 0) return;

        Log($"🔴 [{asset}] SELL: {sellVolume} @ ~${currentPrice:F2}");

        _ = Task.Run(() => ThrottledRestCallAsync(
            () => ExecuteOrderViaRestAsync(asset, "SELL", sellVolume)));
    }

    // ══════════════════════════════════════════════════════════
    //  VOLUME ROUNDING
    // ══════════════════════════════════════════════════════════

    private decimal RoundVolume(string asset, decimal volume)
    {
        var key = asset.ToUpperInvariant();

        if (_symbolFilters.TryGetValue(key, out var filter))
        {
            decimal stepped = Math.Floor(volume / filter.StepSize) * filter.StepSize;
            if (stepped < filter.MinQty)
            {
                Log($"⚠️ [{asset}] Volume {stepped} below minQty {filter.MinQty}");
                return 0;
            }
            return Math.Round(stepped, filter.StepDecimals);
        }

        Log($"⚠️ [{asset}] No exchange info, using fallback rounding");
        if (asset.StartsWith("BTC", StringComparison.OrdinalIgnoreCase))
            return Math.Round(volume, 5);
        if (asset.StartsWith("ETH", StringComparison.OrdinalIgnoreCase))
            return Math.Round(volume, 4);
        return Math.Round(volume, 4);
    }

    // ══════════════════════════════════════════════════════════
    //  POSITION MANAGEMENT
    // ══════════════════════════════════════════════════════════

    private async Task CloseAllPositionsAsync()
    {
        Log("🔄 Closing all open positions...");

        var closeTasks = new List<Task>();

        lock (_lock)
        {
            foreach (var asset in _config.Assets)
            {
                var position = _riskManager?.GetPosition(asset);
                if (position == null) continue;

                decimal price = _lastPrices.GetValueOrDefault(asset, position.EntryPrice);
                decimal sellVolume = RoundVolume(asset, position.Volume);

                if (sellVolume <= 0)
                {
                    Log($"⚠️ [{asset}] Cannot close — volume rounds to 0");
                    continue;
                }

                Log($"🔴 [{asset}] Closing position: {sellVolume} @ ~${price:F2}");
                closeTasks.Add(ExecuteOrderViaRestAsync(asset, "SELL", sellVolume));
            }
        }

        if (closeTasks.Count > 0)
        {
            Log($"⏳ Waiting for {closeTasks.Count} position close(s)...");
            try
            {
                await Task.WhenAll(closeTasks).WaitAsync(TimeSpan.FromSeconds(15));
                Log($"✅ All position closes completed.");
            }
            catch (TimeoutException)
            {
                Log("⚠️ Position close timed out after 15s. " +
                    "Some positions may remain open.");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Position close error: {ex.Message}");
            }
        }
        else
        {
            Log("ℹ️ No open positions to close.");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  ASSET MATCHING (exact)
    // ══════════════════════════════════════════════════════════

    private string? FindAsset(string securityCode)
    {
        if (string.IsNullOrEmpty(securityCode)) return null;
        foreach (var asset in _config.Assets)
        {
            if (securityCode.Equals(asset, StringComparison.OrdinalIgnoreCase))
                return asset;
        }
        return null;
    }

    // ══════════════════════════════════════════════════════════
    //  REST RATE LIMITING (P1 FIX: no fire-and-forget release)
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// P1 FIX: Pre-call delay, immediate release.
    ///
    /// Old approach:
    ///   acquire → call → finally { Task.Run(delay → release) }
    ///   Problem: background task outlives Dispose(), throws ObjectDisposedException.
    ///
    /// New approach:
    ///   acquire → delay if needed → call → finally { release immediately }
    ///   The delay happens BEFORE the call, and the semaphore is released
    ///   synchronously in the finally block. No fire-and-forget tasks.
    /// </summary>
    private async Task<T> ThrottledRestCallAsync<T>(Func<Task<T>> action)
    {
        await _restThrottle.WaitAsync(_cts.Token);
        try
        {
            var elapsed = (DateTime.UtcNow - _lastRestCallTime).TotalMilliseconds;
            if (elapsed < RestDelayMs)
            {
                int waitMs = RestDelayMs - (int)elapsed;
                await Task.Delay(waitMs, _cts.Token);
            }

            var result = await action();
            _lastRestCallTime = DateTime.UtcNow;
            return result;
        }
        finally
        {
            _restThrottle.Release();
        }
    }

    private async Task ThrottledRestCallAsync(Func<Task> action)
    {
        await _restThrottle.WaitAsync(_cts.Token);
        try
        {
            var elapsed = (DateTime.UtcNow - _lastRestCallTime).TotalMilliseconds;
            if (elapsed < RestDelayMs)
            {
                int waitMs = RestDelayMs - (int)elapsed;
                await Task.Delay(waitMs, _cts.Token);
            }

            await action();
            _lastRestCallTime = DateTime.UtcNow;
        }
        finally
        {
            _restThrottle.Release();
        }
    }

    // ══════════════════════════════════════════════════════════
    //  STATUS REPORTING
    // ══════════════════════════════════════════════════════════

    private async Task StatusReportLoop()
    {
        while (_isRunning)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(30), _cts.Token); }
            catch (OperationCanceledException) { break; }

            if (!_isRunning) break;

            lock (_lock)
            {
                Log("══════════════════════════════════════════");
                Log("  📊 STATUS REPORT");
                Log($"  Balance: ${_currentBalance:F2} (Initial: ${_initialBalance:F2})");
                Log($"  Total PnL: ${_totalRealizedPnl:F4} ({GetTotalReturnPercent():F2}%)");
                Log($"  Trades: {_totalTrades} (W:{_winningTrades} L:{_losingTrades} " +
                    $"WR:{GetWinRate():F1}%)");
                Log($"  Daily PnL: ${_riskManager?.DailyPnl:F4}");
                Log($"  Open: {_riskManager?.OpenPositionCount ?? 0}");

                foreach (var asset in _config.Assets)
                {
                    var pos = _riskManager?.GetPosition(asset);
                    if (pos != null)
                    {
                        decimal curPrice = _lastPrices.GetValueOrDefault(asset, pos.EntryPrice);
                        double pnlPct = pos.GetUnrealizedPnlPercent(curPrice);
                        Log($"    [{asset}] {pos} | Now: ${curPrice:F2} | PnL: {pnlPct:F2}%");
                    }
                }
                Log("══════════════════════════════════════════");
            }

            // P2 Fix: Write heartbeat file for Docker healthcheck.
            // docker-compose healthcheck uses: find /app/state/heartbeat -mmin -60
            // If this file hasn't been updated in 60 minutes, the container is
            // considered unhealthy and Docker restarts it.
            try
            {
                var heartbeatPath = Path.Combine(_stateDirectory, "heartbeat");
                File.WriteAllText(heartbeatPath, DateTime.UtcNow.ToString("O"));
            }
            catch { /* best-effort — don't crash the status loop */ }            
        }
    }

    private void PrintFinalReport()
    {
        Log("");
        Log("╔══════════════════════════════════════════════════╗");
        Log("║           FINAL SESSION REPORT                  ║");
        Log("╠══════════════════════════════════════════════════╣");
        Log($"║  Initial Balance:  ${_initialBalance,10:F2}              ║");
        Log($"║  Final Balance:    ${_currentBalance,10:F2}              ║");
        Log($"║  Total PnL:        ${_totalRealizedPnl,10:F4}            ║");
        Log($"║  Return:            {GetTotalReturnPercent(),9:F2}%             ║");
        Log($"║  Total Trades:      {_totalTrades,5}                    ║");
        Log($"║  Winning:           {_winningTrades,5}                    ║");
        Log($"║  Losing:            {_losingTrades,5}                    ║");
        Log($"║  Win Rate:          {GetWinRate(),8:F1}%               ║");
        Log("╚══════════════════════════════════════════════════╝");
    }

    private double GetWinRate() =>
        _totalTrades > 0 ? (double)_winningTrades / _totalTrades * 100.0 : 0;

    private double GetTotalReturnPercent() =>
        _initialBalance > 0 ? (double)(_totalRealizedPnl / _initialBalance) * 100.0 : 0;

    // ══════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════

    private static SecureString ToSecureString(string str)
    {
        var ss = new SecureString();
        foreach (var c in str) ss.AppendChar(c);
        ss.MakeReadOnly();
        return ss;
    }

    private void Log(string message)
    {
        string timestamped = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}";
        Console.WriteLine(timestamped);

        lock (_logLock)
        {
            try { File.AppendAllText(_logFile, timestamped + Environment.NewLine); }
            catch { }
        }
    }

    private string GetBinanceInterval() => _config.CandleTimeframeMinutes switch
    {
        1 => "1m", 3 => "3m", 5 => "5m", 15 => "15m",
        30 => "30m", 60 => "1h", 240 => "4h", 1440 => "1d",
        _ => "15m"
    };

    private static int CountDecimals(decimal value)
    {
        if (value == 0) return 0;
        string s = value.ToString(CultureInfo.InvariantCulture);
        int dotIdx = s.IndexOf('.');
        if (dotIdx < 0) return 0;
        return s[(dotIdx + 1)..].TrimEnd('0').Length;
    }

    public void Dispose()
    {
        _isRunning = false;
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        try { _restThrottle.Dispose(); } catch { }
        try { _adapter?.Dispose(); } catch { }
    }

    // ══════════════════════════════════════════════════════════
    //  REST: Balance
    // ══════════════════════════════════════════════════════════

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private async Task<decimal?> FetchUsdtBalanceAsync()
    {
        var baseUrl = _config.UseTestnet
            ? "https://testnet.binance.vision"
            : "https://api.binance.com";

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var query = $"timestamp={ts}&recvWindow=5000";
        var sig = SignHmacSha256(query, _config.ApiSecret);

        var url = $"{baseUrl}/api/v3/account?{query}&signature={sig}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-MBX-APIKEY", _config.ApiKey);

        var resp = await _http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        foreach (var bal in doc.RootElement.GetProperty("balances").EnumerateArray())
        {
            if (bal.GetProperty("asset").GetString() == "USDT")
            {
                var freeStr = bal.GetProperty("free").GetString() ?? "0";
                return decimal.Parse(freeStr, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static string SignHmacSha256(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ══════════════════════════════════════════════════════════
    //  REST: Exchange Info
    // ══════════════════════════════════════════════════════════

    private async Task LoadExchangeInfoAsync()
    {
        try
        {
            var baseUrl = _config.UseTestnet
                ? "https://testnet.binance.vision"
                : "https://api.binance.com";

            var symbols = string.Join(",",
                _config.Assets.Distinct().Select(a => $"%22{a}%22"));
            var url = $"{baseUrl}/api/v3/exchangeInfo?symbols=[{symbols}]";

            Log("📋 Fetching exchange info...");

            var resp = await _http.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(json);
            foreach (var sym in doc.RootElement.GetProperty("symbols").EnumerateArray())
            {
                var symbol = sym.GetProperty("symbol").GetString() ?? "";
                decimal stepSize = 0, minQty = 0, minNotional = 0;

                foreach (var filter in sym.GetProperty("filters").EnumerateArray())
                {
                    var ft = filter.GetProperty("filterType").GetString();

                    if (ft == "LOT_SIZE")
                    {
                        stepSize = decimal.Parse(
                            filter.GetProperty("stepSize").GetString() ?? "0.0001",
                            CultureInfo.InvariantCulture);
                        minQty = decimal.Parse(
                            filter.GetProperty("minQty").GetString() ?? "0",
                            CultureInfo.InvariantCulture);
                    }
                    else if (ft is "NOTIONAL" or "MIN_NOTIONAL")
                    {
                        if (filter.TryGetProperty("minNotional", out var mn))
                            minNotional = decimal.Parse(
                                mn.GetString() ?? "0", CultureInfo.InvariantCulture);
                    }
                }

                if (stepSize > 0)
                {
                    _symbolFilters[symbol.ToUpperInvariant()] = new SymbolFilter
                    {
                        StepSize = stepSize,
                        MinQty = minQty,
                        MinNotional = minNotional,
                        StepDecimals = CountDecimals(stepSize)
                    };
                    Log($"📋 [{symbol}] step={stepSize} minQty={minQty} minNotional={minNotional}");
                }
            }

            _exchangeInfoLoaded = true;
            Log($"📋 Exchange info loaded for {_symbolFilters.Count} symbols");
        }
        catch (Exception ex)
        {
            Log($"⚠️ Exchange info failed: {ex.Message}. Using fallback rounding.");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  REST: Historical Candles
    // ══════════════════════════════════════════════════════════

    private async Task<long?> PreloadHistoricalCandles(string asset, int count = 50)
    {
        long? lastOpenTimeMs = null;

        try
        {
            var baseUrl = _config.UseTestnet
                ? "https://testnet.binance.vision"
                : "https://api.binance.com";

            var url = $"{baseUrl}/api/v3/klines?symbol={asset}" +
                      $"&interval={GetBinanceInterval()}&limit={count}";

            Log($"📥 [{asset}] Fetching {count} historical candles...");

            var resp = await _http.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(json);
            var candles = doc.RootElement.EnumerateArray().ToList();

            Log($"📥 [{asset}] Received {candles.Count} candles");

            if (candles.Count < 2)
            {
                Log($"⚠️ [{asset}] Not enough candles");
                return null;
            }

            for (int i = 0; i < candles.Count - 1; i++)
            {
                var k = candles[i];
                var open = decimal.Parse(k[1].GetString()!, CultureInfo.InvariantCulture);
                var high = decimal.Parse(k[2].GetString()!, CultureInfo.InvariantCulture);
                var low = decimal.Parse(k[3].GetString()!, CultureInfo.InvariantCulture);
                var close = decimal.Parse(k[4].GetString()!, CultureInfo.InvariantCulture);
                var volume = decimal.Parse(k[5].GetString()!, CultureInfo.InvariantCulture);
                var openMs = k[0].GetInt64();
                var openTime = DateTimeOffset.FromUnixTimeMilliseconds(openMs);

                lastOpenTimeMs = openMs;

                lock (_lock)
                {
                    if (_traders.TryGetValue(asset, out var trader))
                        trader.ProcessCandle(open, high, low, close, volume, openTime);
                }
            }

            if (lastOpenTimeMs.HasValue)
            {
                var lastClose = decimal.Parse(
                    candles[^2][4].GetString()!, CultureInfo.InvariantCulture);
                _lastPrices[asset] = lastClose;

                bool warmedUp = _traders.TryGetValue(asset, out var t) && t.IsWarmedUp;
                Log($"📥 [{asset}] Complete. Price=${lastClose:F2} Warmed={warmedUp}");
                t?.GoLive();
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ [{asset}] Historical fetch failed: {ex.Message}");
        }

        return lastOpenTimeMs;
    }

    // ══════════════════════════════════════════════════════════
    //  REST: Candle Polling Loop
    // ══════════════════════════════════════════════════════════

    private async Task CandlePollingLoop()
    {
        while (_isRunning && !_tradersInitialized)
        {
            try { await Task.Delay(2000, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }

        var lastCandleTime = new Dictionary<string, long>();

        foreach (var asset in _config.Assets.Distinct())
        {
            if (_cts.IsCancellationRequested) return;
            var lastTime = await ThrottledRestCallAsync(
                () => PreloadHistoricalCandles(asset, 100));
            if (lastTime.HasValue)
            {
                lastCandleTime[asset] = lastTime.Value;
                Log($"📥 [{asset}] Guard at " +
                    $"{DateTimeOffset.FromUnixTimeMilliseconds(lastTime.Value):yyyy-MM-dd HH:mm}");
            }
        }

        // ── P1 FIX: Check restored positions against current prices ──
        // At this point, historical candles have been loaded and
        // _lastPrices are populated. Check if any persisted positions
        // are in trouble before the first live candle arrives.
        lock (_lock)
        {
            if (_riskManager != null && _riskManager.OpenPositionCount > 0)
            {
                Log("🔍 Checking restored positions against current prices...");
                var warnings = _riskManager.CheckRestoredPositions(
                    asset => _lastPrices.GetValueOrDefault(asset, 0));

                foreach (var w in warnings)
                {
                    string emoji = w.Severity switch
                    {
                        PositionWarningSeverity.Critical => "🚨",
                        PositionWarningSeverity.Warning => "⚠️",
                        _ => "ℹ️"
                    };
                    Log($"{emoji} [{w.Asset}] {w.Message}");
                }

                if (warnings.Count == 0)
                    Log("✅ All restored positions within normal parameters.");
            }
        }

        // Immediately poll for candle that completed during warmup
        foreach (var asset in _config.Assets.Distinct())
        {
            if (_cts.IsCancellationRequested) return;
            try { await ThrottledRestCallAsync(() => PollLatestCandle(asset, lastCandleTime)); }
            catch (Exception ex) { Log($"⚠️ [{asset}] Post-warmup poll: {ex.Message}"); }
        }

        Log("🔄 Starting REST candle polling loop...");

        while (_isRunning && !_cts.IsCancellationRequested)
        {
            long intervalMs = _config.CandleTimeframeMinutes * 60 * 1000L;
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long nextClose = ((nowMs / intervalMs) + 1) * intervalMs;
            long waitMs = (nextClose - nowMs) + 5000;
            if (waitMs < 3000) waitMs += intervalMs;

            Log($"⏰ Next poll ~{DateTimeOffset.UtcNow.AddMilliseconds(waitMs):HH:mm:ss} UTC " +
                $"({waitMs / 1000}s)");

            try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), _cts.Token); }
            catch (OperationCanceledException) { break; }

            if (!_isRunning) break;

            foreach (var asset in _config.Assets.Distinct())
            {
                if (_cts.IsCancellationRequested) break;
                try
                {
                    await ThrottledRestCallAsync(() => PollLatestCandle(asset, lastCandleTime));
                }
                catch (Exception ex)
                {
                    Log($"⚠️ [{asset}] Poll error: {ex.Message}");
                }
            }
        }

        Log("🔄 Polling loop stopped.");
    }

    private async Task PollLatestCandle(string asset, Dictionary<string, long> lastCandleTime)
    {
        var baseUrl = _config.UseTestnet
            ? "https://testnet.binance.vision"
            : "https://api.binance.com";

        var url = $"{baseUrl}/api/v3/klines?symbol={asset}" +
                  $"&interval={GetBinanceInterval()}&limit=3";

        var resp = await _http.GetAsync(url);
        var json = await resp.Content.ReadAsStringAsync();
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        var candles = doc.RootElement.EnumerateArray().ToList();

        if (candles.Count < 2) return;

        var k = candles[^2];
        var openTimeMs = k[0].GetInt64();

        if (lastCandleTime.TryGetValue(asset, out var prev) && openTimeMs <= prev)
            return;

        lastCandleTime[asset] = openTimeMs;

        var open = decimal.Parse(k[1].GetString()!, CultureInfo.InvariantCulture);
        var high = decimal.Parse(k[2].GetString()!, CultureInfo.InvariantCulture);
        var low = decimal.Parse(k[3].GetString()!, CultureInfo.InvariantCulture);
        var close = decimal.Parse(k[4].GetString()!, CultureInfo.InvariantCulture);
        var volume = decimal.Parse(k[5].GetString()!, CultureInfo.InvariantCulture);
        var openTime = DateTimeOffset.FromUnixTimeMilliseconds(openTimeMs);

        Log($"🕯️ [REST] [{asset}] {openTime:yyyy-MM-dd HH:mm} " +
            $"O={open} H={high} L={low} C={close} V={volume:F2}");

        lock (_lock)
        {
            if (_traders.TryGetValue(asset, out var trader))
                trader.ProcessCandle(open, high, low, close, volume, openTime);
            _lastPrices[asset] = close;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  REST: Order Execution
    // ══════════════════════════════════════════════════════════

    private async Task<bool> ExecuteOrderViaRestAsync(
        string asset, string side, decimal quantity)
    {
        try
        {
            var baseUrl = _config.UseTestnet
                ? "https://testnet.binance.vision"
                : "https://api.binance.com";

            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var qp = $"symbol={asset}&side={side}&type=MARKET" +
                     $"&quantity={quantity.ToString(CultureInfo.InvariantCulture)}" +
                     $"&timestamp={ts}&recvWindow=5000";

            var sig = SignHmacSha256(qp, _config.ApiSecret);
            var url = $"{baseUrl}/api/v3/order?{qp}&signature={sig}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-MBX-APIKEY", _config.ApiKey);

            Log($"📡 [{asset}] {side} {quantity} via REST...");

            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Log($"❌ [{asset}] Order failed: {resp.StatusCode} — {json}");
                return false;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var orderId = root.GetProperty("orderId").GetInt64();
            var status = root.GetProperty("status").GetString();
            var filledQty = decimal.Parse(
                root.GetProperty("executedQty").GetString() ?? "0",
                CultureInfo.InvariantCulture);
            var filledQuote = decimal.Parse(
                root.GetProperty("cummulativeQuoteQty").GetString() ?? "0",
                CultureInfo.InvariantCulture);
            var avgPrice = filledQty > 0 ? filledQuote / filledQty : 0;

            Log($"✅ [{asset}] FILLED ID={orderId} {status} " +
                $"Qty={filledQty} Avg=${avgPrice:F2} Total=${filledQuote:F2}");

            lock (_orderIdLock)
            {
                _processedOrderIds[orderId] = DateTime.UtcNow;
            }

            lock (_lock)
            {
                ProcessFill(asset, side, avgPrice, filledQty);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"❌ [{asset}] REST order error: {ex.Message}");
            return false;
        }
    }
}

// ══════════════════════════════════════════════════════════
//  SUPPORT TYPES
// ══════════════════════════════════════════════════════════

public class BufferedCandle
{
    public string SecurityCode { get; set; } = "";
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public DateTimeOffset OpenTime { get; set; }
}

public class SymbolFilter
{
    public decimal StepSize { get; set; }
    public decimal MinQty { get; set; }
    public decimal MinNotional { get; set; }
    public int StepDecimals { get; set; }
}
                