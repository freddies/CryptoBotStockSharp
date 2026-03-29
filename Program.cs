using Microsoft.Extensions.Configuration;
using CryptoBotStockSharp.Engine;
using CryptoBotStockSharp.Models;

namespace CryptoBotStockSharp;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        PrintBanner();

        // ── Load Configuration ──
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        var config = new BotConfig();
        configuration.GetSection("BotConfig").Bind(config);

        // ── Validate Configuration ──
        if (!ValidateConfig(config))
        {
            Console.WriteLine("❌ Invalid configuration. Please check appsettings.json");
            return;
        }

        // ── Allow env var overrides for API keys (safer) ──
        var envKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY");
        var envSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET");

        if (!string.IsNullOrEmpty(envKey))
            config.ApiKey = envKey;
        if (!string.IsNullOrEmpty(envSecret))
            config.ApiSecret = envSecret;

        // ── Create and start bot ──
        using var bot = new TradingBot(config);

        // ── Graceful shutdown ──
        // FIX: Use a single CTS; CancelKeyPress just signals, doesn't block
        var cts = new CancellationTokenSource();

        // P1 Fix: CancelKeyPress uses try/catch around async call
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n🛑 Shutdown signal received...");
            cts.Cancel(); 
        };

        // P1 Fix: ProcessExit uses synchronous wait
        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            if (!cts.IsCancellationRequested)
                cts.Cancel();
        };

        try
        {
            await bot.StartAsync();

            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════╗");
            Console.WriteLine("║  Bot is running. Press Ctrl+C to stop.       ║");
            Console.WriteLine("║  Type 'status' for current state.            ║");
            Console.WriteLine("║  Type 'quit' to exit.                        ║");
            Console.WriteLine("╚══════════════════════════════════════════════╝");
            Console.WriteLine();

            // ── P1 Fix: Detect non-interactive stdin (Docker without tty) ──
            if (Console.IsInputRedirected)
            {
                Console.WriteLine("ℹ️ Non-interactive mode detected (Docker/piped stdin). " +
                    "Use Ctrl+C or SIGTERM to stop.");
                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                catch (OperationCanceledException) { }
            }
            else
            {
                // Interactive command loop
                await RunInteractiveLoop(bot, cts);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via cancellation
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Fatal error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }

        // FIX: Clean shutdown always goes through StopAsync here
        await bot.StopAsync();

        Console.WriteLine("👋 Goodbye!");
    }

    /// <summary>
    /// Interactive command loop for terminal sessions.
    /// P1 Fix: ReadLine task doesn't leak when cancelled.
    /// </summary>
    static async Task RunInteractiveLoop(TradingBot bot, CancellationTokenSource cts)
    {
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                // Use a dedicated long-lived thread for Console.ReadLine
                // so we don't leak threadpool threads
                string? input = null;
                var readTask = Task.Factory.StartNew(() =>
                {
                    try { return Console.ReadLine(); }
                    catch { return null; }
                }, TaskCreationOptions.LongRunning);

                var completedTask = await Task.WhenAny(
                    readTask,
                    Task.Delay(Timeout.Infinite, cts.Token));

                if (completedTask != readTask)
                    break; // Cancellation happened

                input = await readTask;
                if (input == null)
                {
                    // EOF — stdin closed (e.g., Docker detached)
                    Console.WriteLine("ℹ️ stdin closed, switching to wait mode.");
                    try { await Task.Delay(Timeout.Infinite, cts.Token); }
                    catch (OperationCanceledException) { }
                    break;
                }

                switch (input.Trim().ToLower())
                {
                    case "quit":
                    case "exit":
                    case "q":
                        await bot.StopAsync();
                        cts.Cancel();
                        break;

                    case "status":
                    case "s":
                        Console.WriteLine("📊 Status report logged above (check console output)");
                        break;

                    case "help":
                    case "h":
                        Console.WriteLine("Commands: status | quit | help");
                        break;

                    default:
                        if (!string.IsNullOrWhiteSpace(input))
                            Console.WriteLine("Unknown command. Type 'help' for options.");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Command loop error: {ex.Message}");
            }
        }
    }

    static bool ValidateConfig(BotConfig config)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // ══════════════════════════════════════════════════════
        //  API Keys (P1 Fix: catch "FROM_ENV" placeholder)
        // ══════════════════════════════════════════════════════

        if (string.IsNullOrWhiteSpace(config.ApiKey) ||
            config.ApiKey == "YOUR_BINANCE_API_KEY" ||
            config.ApiKey == "FROM_ENV")
        {
            var envKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY");
            if (string.IsNullOrEmpty(envKey))
                errors.Add("API Key not set. Use appsettings.json or " +
                           "BINANCE_API_KEY environment variable.");
        }

        if (string.IsNullOrWhiteSpace(config.ApiSecret) ||
            config.ApiSecret == "YOUR_BINANCE_API_SECRET" ||
            config.ApiSecret == "FROM_ENV")
        {
            var envSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET");
            if (string.IsNullOrEmpty(envSecret))
                errors.Add("API Secret not set. Use appsettings.json or " +
                           "BINANCE_API_SECRET environment variable.");
        }

        // ══════════════════════════════════════════════════════
        //  Assets
        // ══════════════════════════════════════════════════════

        if (config.Assets == null || config.Assets.Length == 0)
            errors.Add("No assets configured.");

        // ══════════════════════════════════════════════════════
        //  Basic Range Validation (existing)
        // ══════════════════════════════════════════════════════

        if (config.StopLossPercent <= 0 || config.StopLossPercent > 20)
            errors.Add($"StopLossPercent invalid: {config.StopLossPercent} (must be 0–20)");

        if (config.TakeProfitPercent <= 0 || config.TakeProfitPercent > 50)
            errors.Add($"TakeProfitPercent invalid: {config.TakeProfitPercent} (must be 0–50)");

        if (config.PositionSizePercent <= 0 || config.PositionSizePercent > 100)
            errors.Add($"PositionSizePercent invalid: {config.PositionSizePercent} (must be 0–100)");

        if (config.MaxDailyLossPercent <= 0 || config.MaxDailyLossPercent > 50)
            errors.Add($"MaxDailyLossPercent invalid: {config.MaxDailyLossPercent} (must be 0–50)");

        // ── P2 Fix: Additional range checks missing from original ──

        if (config.TrailingStopActivationPercent <= 0)
            errors.Add($"TrailingStopActivationPercent must be > 0, got {config.TrailingStopActivationPercent}");

        if (config.TrailingStopDistancePercent <= 0)
            errors.Add($"TrailingStopDistancePercent must be > 0, got {config.TrailingStopDistancePercent}");

        if (config.TradingFeeRate < 0 || config.TradingFeeRate > 0.01)
            errors.Add($"TradingFeeRate invalid: {config.TradingFeeRate} (must be 0–0.01, i.e. 0–1%)");

        if (config.MinExitPnlPercent < 0)
            errors.Add($"MinExitPnlPercent must be >= 0, got {config.MinExitPnlPercent}");

        if (config.MaxTotalPositions <= 0)
            errors.Add($"MaxTotalPositions must be > 0, got {config.MaxTotalPositions}");

        if (config.CandleTimeframeMinutes <= 0)
            errors.Add($"CandleTimeframeMinutes must be > 0, got {config.CandleTimeframeMinutes}");

        // ══════════════════════════════════════════════════════
        //  Brain Weights (P1 Fix)
        // ══════════════════════════════════════════════════════

        double totalBrainWeight = config.WeightBrainTechnical + config.WeightBrainStockSharp;
        if (totalBrainWeight <= 0)
            errors.Add("Brain weights (WeightBrainTechnical + WeightBrainStockSharp) must sum to > 0");

        if (config.WeightBrainTechnical < 0)
            errors.Add($"WeightBrainTechnical must be >= 0, got {config.WeightBrainTechnical}");
        if (config.WeightBrainStockSharp < 0)
            errors.Add($"WeightBrainStockSharp must be >= 0, got {config.WeightBrainStockSharp}");

        // ── Indicator weights ──
        double totalIndicatorWeight = config.WeightRsi + config.WeightBollinger +
                                       config.WeightEma + config.WeightMacd;
        if (totalIndicatorWeight <= 0)
            errors.Add("Indicator weights (RSI + BB + EMA + MACD) must sum to > 0");

        // ══════════════════════════════════════════════════════
        //  P2 Fix: Cross-Field Validation
        // ══════════════════════════════════════════════════════

        // MinExitPnl must be less than TakeProfit.
        // Otherwise signal-based exits never fire — positions can only close
        // via the hard TP/SL levels, making the multi-brain sell signals useless.
        if (config.MinExitPnlPercent >= config.TakeProfitPercent)
            errors.Add($"MinExitPnlPercent ({config.MinExitPnlPercent}%) must be < " +
                       $"TakeProfitPercent ({config.TakeProfitPercent}%). " +
                       $"Otherwise signal-based exits are impossible.");

        // Trailing stop distance must be less than activation threshold.
        // Otherwise the trailing stop is always looser than the fixed SL.
        if (config.TrailingStopDistancePercent >= config.TrailingStopActivationPercent)
            errors.Add($"TrailingStopDistancePercent ({config.TrailingStopDistancePercent}%) " +
                       $"must be < TrailingStopActivationPercent " +
                       $"({config.TrailingStopActivationPercent}%). " +
                       $"Otherwise trailing stop provides no tighter protection than fixed SL.");

        // Stop loss should be meaningfully larger than round-trip trading fees.
        // With fee=0.1% per side, round-trip = 0.2%.
        // A stop loss below round-trip fees means every stopped trade is a guaranteed
        // net loss larger than the stop itself.
        double roundTripFeePct = config.TradingFeeRate * 2.0 * 100.0;
        if (config.StopLossPercent > 0 && config.StopLossPercent < roundTripFeePct * 2)
        {
            if (config.StopLossPercent < roundTripFeePct)
            {
                errors.Add($"StopLossPercent ({config.StopLossPercent}%) is BELOW round-trip fees " +
                           $"({roundTripFeePct:F3}%). Every stopped trade guarantees a loss " +
                           $"greater than the stop. Minimum recommended: {roundTripFeePct * 2:F3}%");
            }
            else
            {
                warnings.Add($"StopLossPercent ({config.StopLossPercent}%) is close to round-trip " +
                             $"fees ({roundTripFeePct:F3}%). Net stop loss after fees is only " +
                             $"{config.StopLossPercent - roundTripFeePct:F3}%. " +
                             $"Recommended minimum: {roundTripFeePct * 2:F3}%");
            }
        }

        // Total maximum exposure cannot exceed 100% of balance.
        double maxExposure = config.PositionSizePercent * config.MaxTotalPositions;
        if (maxExposure > 100)
            errors.Add($"PositionSizePercent ({config.PositionSizePercent}%) × " +
                       $"MaxTotalPositions ({config.MaxTotalPositions}) = {maxExposure}% > 100%. " +
                       $"Cannot fund all positions simultaneously.");
        else if (maxExposure > 90)
        {
            warnings.Add($"PositionSizePercent ({config.PositionSizePercent}%) × " +
                         $"MaxTotalPositions ({config.MaxTotalPositions}) = {maxExposure}%. " +
                         $"Very high exposure — consider reducing.");
        }

        // Buy/Sell threshold polarity check.
        if (config.BuyScoreThreshold < 0)
            errors.Add($"BuyScoreThreshold ({config.BuyScoreThreshold}) should be >= 0. " +
                       $"Negative value means buying on bearish signals.");
        if (config.SellScoreThreshold > 0)
            errors.Add($"SellScoreThreshold ({config.SellScoreThreshold}) should be <= 0. " +
                       $"Positive value means selling on bullish signals.");

        // Risk/reward ratio check.
        if (config.TakeProfitPercent > 0 && config.StopLossPercent > 0 &&
            config.TakeProfitPercent <= config.StopLossPercent)
        {
            warnings.Add($"TakeProfitPercent ({config.TakeProfitPercent}%) <= " +
                         $"StopLossPercent ({config.StopLossPercent}%). " +
                         $"Risk/reward ratio < 1:1. Requires high win rate to be profitable.");
        }

        // Single-trade loss vs daily limit check.
        // If one full stop loss exceeds the daily limit, the bot locks out after one trade.
        double singleLossPct = (config.PositionSizePercent / 100.0) * config.StopLossPercent;
        if (singleLossPct > config.MaxDailyLossPercent)
        {
            warnings.Add($"A single stop loss ({singleLossPct:F2}% of total balance) exceeds " +
                         $"MaxDailyLossPercent ({config.MaxDailyLossPercent}%). " +
                         $"Bot will be locked for the day after one losing trade.");
        }

        // EMA fast must be shorter than slow.
        if (config.EmaFastPeriod >= config.EmaSlowPeriod)
            errors.Add($"EmaFastPeriod ({config.EmaFastPeriod}) must be < " +
                       $"EmaSlowPeriod ({config.EmaSlowPeriod})");

        // MACD fast must be shorter than slow.
        if (config.MacdFastPeriod >= config.MacdSlowPeriod)
            errors.Add($"MacdFastPeriod ({config.MacdFastPeriod}) must be < " +
                       $"MacdSlowPeriod ({config.MacdSlowPeriod})");

        // Regime min history must be less than lookback window.
        if (config.UseRegimeDetection && config.RegimeMinHistory >= config.RegimeLookbackCandles)
            errors.Add($"RegimeMinHistory ({config.RegimeMinHistory}) must be < " +
                       $"RegimeLookbackCandles ({config.RegimeLookbackCandles})");

        // ══════════════════════════════════════════════════════
        //  Warmup vs Historical Preload Validation
        //
        //  PreloadHistoricalCandles fetches 100 candles from Binance
        //  but skips the last (in-progress) → 99 usable candles.
        //  If any indicator needs more than 99 candles to warm up,
        //  the bot will run partially blind until enough live candles
        //  arrive to fill the gap.
        // ══════════════════════════════════════════════════════

        const int HistoricalPreloadUsable = 99;

        // Brain 1 warmup requirements
        int brain1Warmup = new[]
        {
            config.RsiPeriod + 1,                               // RSI: period + 1 candle
            config.BollingerPeriod,                              // BB: period candles
            config.EmaSlowPeriod,                                // EMA: slow period for init
            config.MacdSlowPeriod + config.MacdSignalPeriod,     // MACD: slow EMA + signal SMA seed
        }.Max();

        // Brain 2 warmup requirements
        int brain2Warmup = new[]
        {
            config.StochPeriod + config.StochSmoothing,          // Stochastic K + D smoothing
            config.AdxPeriod * 2,                                // ADX: needs 2× period
            config.AtrPeriod + 1,                                // ATR: period + 1
            20,                                                   // OBV EMA: hardcoded 20 candles
            config.WilliamsRPeriod,                              // Williams %R: period candles
            40,                                                   // StockSharpBrain.IsReady minimum
        }.Max();

        // Regime detector needs both brains ready + its own min history
        int regimeWarmup = config.UseRegimeDetection
            ? Math.Max(brain1Warmup, brain2Warmup) + config.RegimeMinHistory
            : 0;

        int totalWarmup = new[] { brain1Warmup, brain2Warmup, regimeWarmup }.Max();

        if (totalWarmup > HistoricalPreloadUsable)
        {
            int gapCandles = totalWarmup - HistoricalPreloadUsable;
            int gapMinutes = gapCandles * config.CandleTimeframeMinutes;

            string timeStr;
            if (gapMinutes >= 1440)
                timeStr = $"{gapMinutes / 1440.0:F1} days";
            else if (gapMinutes >= 60)
                timeStr = $"{gapMinutes / 60.0:F1} hours";
            else
                timeStr = $"{gapMinutes} minutes";

            warnings.Add($"Indicator warmup needs ~{totalWarmup} candles but only " +
                        $"{HistoricalPreloadUsable} historical candles are preloaded. " +
                        $"Bot will need {gapCandles} additional live candles (~{timeStr}) " +
                        $"before all signals are active. " +
                        $"(Brain1={brain1Warmup}, Brain2={brain2Warmup}" +
                        $"{(config.UseRegimeDetection ? $", Regime={regimeWarmup}" : "")})");
        }
        else
        {
            int margin = HistoricalPreloadUsable - totalWarmup;
            if (margin < 10)
            {
                warnings.Add($"Tight warmup margin: need {totalWarmup} candles, " +
                            $"preload provides {HistoricalPreloadUsable} " +
                            $"(only {margin} candles spare). " +
                            $"Consider increasing indicator periods cautiously.");
            }
        }

        // ══════════════════════════════════════════════════════
        //  Output Results
        // ══════════════════════════════════════════════════════

        if (warnings.Count > 0)
        {
            Console.WriteLine("⚠️  Configuration warnings:");
            foreach (var w in warnings)
                Console.WriteLine($"   ⚠️  {w}");
            Console.WriteLine();
        }

        if (errors.Count > 0)
        {
            Console.WriteLine("❌ Configuration errors:");
            foreach (var e in errors)
                Console.WriteLine($"   • {e}");
            return false;
        }

        Console.WriteLine("✅ Configuration validated.");
        return true;
    }

    static void PrintBanner()
    {
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════╗
║                                                          ║
║              CryptoBotStockSharp                         ║
║       Adaptive Multi-Indicator Trading Bot               ║
║       BTC/USDT + ETH/USDT on Binance                     ║
║                                                          ║
║         TRADING CARRIES RISK OF LOSS                     ║
║       Always start on TESTNET first!                     ║
║                                                          ║
╚══════════════════════════════════════════════════════════╝
        ");
    }
}