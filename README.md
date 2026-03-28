# CryptoBotStockSharp

Adaptive multi-brain cryptocurrency trading bot for Binance, built with .NET 9 and [StockSharp](https://github.com/StockSharp/StockSharp).

Combines two independent technical analysis engines with a market regime detector and meta-decision engine to generate consensus-based trading signals. Supports BTC/USDT, ETH/USDT, and any Binance spot pair.

> **⚠️ TRADING CARRIES RISK OF LOSS.**
> This bot is provided as-is for educational and experimental purposes. Always start on **testnet**. Never risk money you cannot afford to lose.

---

## Table of Contents

- [Architecture](#architecture)
- [Features](#features)
- [Quick Start](#quick-start)
  - [Prerequisites](#prerequisites)
  - [Local Setup](#local-setup)
  - [Docker Setup](#docker-setup)
- [Configuration](#configuration)
  - [API Keys](#api-keys)
  - [Full Config Reference](#full-config-reference)
- [How It Works](#how-it-works)
  - [Brain 1: Technical Indicators](#brain-1-technical-indicators)
  - [Brain 2: StockSharp Indicators](#brain-2-stocksharp-indicators)
  - [Market Regime Detection](#market-regime-detection)
  - [Meta-Decision Engine](#meta-decision-engine)
  - [Risk Management](#risk-management)
- [Signal Flow](#signal-flow)
- [Position Lifecycle](#position-lifecycle)
- [Crash Recovery](#crash-recovery)
- [Monitoring](#monitoring)
- [Project Structure](#project-structure)
- [Roadmap](#roadmap)
- [License](#license)

---

## Architecture

```markdown
┌─────────────────────────────────────────────────────────────┐
│                        TradingBot                           │
│  ┌──────────────┐   ┌──────────────┐  ┌──────────────────┐  │
│  │   Binance    │   │  REST API    │  │   RiskManager    │  │
│  │  WebSocket   │   │  (orders,    │  │  (SL/TP/trail,   │  │
│  │  (candles,   │   │   balance,   │  │   daily limits,  │  │
│  │   level1,    │   │   history)   │  │   persistence)   │  │
│  │   fills)     │   │              │  │                  │  │
│  └──────┬───────┘   └──────┬───────┘  └────────┬─────────┘  │
│         │                  │                   │            │
│         └─────────┬────────┘                   │            │
│                   ▼                            │            │
│  ┌─────────────────────────────────────────────┴──────────┐ │
│  │                    AssetTrader (per asset)             │ │
│  │  ┌─────────────┐  ┌─────────────┐  ┌───────────────┐   │ │
│  │  │   Brain 1   │  │   Brain 2   │  │    Market     │   │ │
│  │  │  RSI, EMA,  │  │  Stoch, ADX │  │    Regime     │   │ │
│  │  │  Bollinger, │  │  ATR, OBV,  │  │   Detector    │   │ │
│  │  │  MACD       │  │  Williams%R │  │               │   │ │
│  │  └──────┬──────┘  └──────┬──────┘  └───────┬───────┘   │ │
│  │         │                │                 │           │ │
│  │         └────────┬───────┘                 │           │ │
│  │                  ▼                         │           │ │
│  │  ┌─────────────────────────────────────────┴────────┐  │ │
│  │  │           MetaDecisionEngine                     │  │ │
│  │  │  Weighted consensus vote → Buy / Sell / None     │  │ │
│  │  └──────────────────────────────────────────────────┘  │ │
│  └────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

---

## Features

### Multi-Brain Analysis
- **Brain 1** — RSI, EMA crossover, Bollinger Bands, MACD with continuous linear scoring (no dead zones)
- **Brain 2** — Stochastic oscillator, ADX/DI directional system, ATR volatility, OBV volume confirmation, Williams %R (medium-term)
- **Weighted consensus** — brains vote with scores and confidence levels; configurable weights

### Market Regime Detection
Automatically classifies market conditions and adjusts brain weights accordingly:

| Regime | Condition | Brain Adjustment |
|--------|-----------|------------------|
| **Compressed** | BB squeezed + ATR low + ADX low | Equal weight, lower thresholds |
| **Ranging** | ADX low, oscillating price | Favor Brain 1 (mean-reversion) |
| **Trending Bull** | ADX high, +DI > -DI, EMA aligned | Favor Brain 2 (trend-following) |
| **Trending Bear** | ADX high, -DI > +DI, EMA aligned | Favor Brain 2, harder to buy |
| **Volatile** | ATR spike, wide BB | Reduce both, raise thresholds |

### Risk Management
- **Stop loss / take profit** with configurable percentages
- **Trailing stop** with activation threshold and ratcheting distance (floored at stop loss)
- **Daily loss limit** based on day-start balance (doesn't drift intra-day)
- **Consecutive loss cooldown** with configurable candle-based timeout
- **Maximum position limits** per asset and total
- **Exchange LOT_SIZE / minNotional** enforcement
- **Configurable trading fee rate** for accurate PnL calculation

### Reliability
- **Crash recovery** — positions and daily state persisted to disk with atomic writes
- **Stale position detection** — validates restored positions against current prices on restart
- **Order deduplication** — timestamped tracking prevents double fills from WebSocket + REST race
- **Candle buffering** — candles arriving before initialization are replayed in order
- **REST rate limiting** — semaphore-based throttle prevents API ban
- **Docker healthcheck** — heartbeat file detects stuck bot state
- **Graceful shutdown** — positions closed before cancellation token fires

### Data Pipeline
- Historical candle preload (100 candles) for indicator warmup
- Dual data source: WebSocket for real-time + REST polling for reliability
- Candle deduplication by timestamp

---

## Quick Start

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (for local) or [Docker](https://www.docker.com/) (for containerized)
- Binance account with API key ([testnet](https://testnet.binance.vision/) recommended)

### Local Setup

```bash
# Clone
git clone https://github.com/freddies/CryptoBotStockSharp.git
cd CryptoBotStockSharp

# Create .env file with your API keys
cat > .env << 'EOF'
BINANCE_API_KEY=your_testnet_api_key_here
BINANCE_API_SECRET=your_testnet_api_secret_here
EOF

# Restore and run
dotnet restore
dotnet run
```

### Docker Setup

```bash
# Clone
git clone https://github.com/YOUR_USERNAME/CryptoBotStockSharp.git
cd CryptoBotStockSharp

# Create .env file
cat > .env << 'EOF'
BINANCE_API_KEY=your_testnet_api_key_here
BINANCE_API_SECRET=your_testnet_api_secret_here
EOF

# Build and run
docker compose up -d

# View logs
docker compose logs -f

# Stop
docker compose down
```

---

## Configuration

### API Keys

API keys are loaded from environment variables (recommended) or `appsettings.json`:

| Method | Priority | How |
|--------|----------|-----|
| Environment variable | Highest | `BINANCE_API_KEY` / `BINANCE_API_SECRET` |
| `.env` file (Docker) | High | Loaded via `docker-compose.yml` `env_file` |
| `appsettings.json` | Lowest | Set `ApiKey` / `ApiSecret` directly (not recommended) |

> **Never commit API keys.** The `.env` file is excluded by `.gitignore`.

### Full Config Reference

All settings in `appsettings.json` under `"BotConfig"`:

#### Connection

| Key | Default | Description |
|-----|---------|-------------|
| `ApiKey` | `"FROM_ENV"` | Binance API key (prefer env var) |
| `ApiSecret` | `"FROM_ENV"` | Binance API secret (prefer env var) |
| `UseTestnet` | `true` | Use Binance testnet (always start here) |

#### Assets & Timeframe

| Key | Default | Description |
|-----|---------|-------------|
| `Assets` | `["BTCUSDT", "ETHUSDT"]` | Trading pairs |
| `CandleTimeframeMinutes` | `15` | Candle interval (1, 3, 5, 15, 30, 60, 240, 1440) |

#### Position Sizing

| Key | Default | Description |
|-----|---------|-------------|
| `PositionSizePercent` | `40.0` | % of balance per trade |
| `MaxPositionsPerAsset` | `1` | Max positions per asset |
| `MaxTotalPositions` | `2` | Max total open positions |
| `MinOrderValueUsd` | `11.0` | Minimum order value in USD |

#### Risk Management

| Key | Default | Description |
|-----|---------|-------------|
| `StopLossPercent` | `2.5` | Stop loss distance from entry |
| `TakeProfitPercent` | `5.0` | Take profit distance from entry |
| `TrailingStopActivationPercent` | `3.0` | PnL% to activate trailing stop |
| `TrailingStopDistancePercent` | `1.5` | Trailing stop distance from high |
| `MaxDailyLossPercent` | `5.0` | Daily loss limit (% of day-start balance) |
| `CooldownAfterLossCandles` | `3` | Base cooldown candles after consecutive losses |
| `MaxConsecutiveLosses` | `3` | Max losses before cooldown triggers |
| `ResetCooldownOnNewDay` | `false` | Reset cooldown at midnight UTC |
| `TradingFeeRate` | `0.001` | Fee per side (0.001 = 0.1%) |
| `MinExitPnlPercent` | `0.3` | Min PnL% to allow signal-based exit |

#### Brain 1: Technical Indicators

| Key | Default | Description |
|-----|---------|-------------|
| `RsiPeriod` | `14` | RSI lookback period |
| `RsiOversold` | `30.0` | RSI oversold threshold |
| `RsiOverbought` | `70.0` | RSI overbought threshold |
| `BollingerPeriod` | `20` | Bollinger Bands lookback |
| `BollingerStdDev` | `2.0` | Bollinger Bands std dev multiplier |
| `EmaFastPeriod` | `9` | Fast EMA period |
| `EmaSlowPeriod` | `21` | Slow EMA period |
| `MacdFastPeriod` | `12` | MACD fast EMA |
| `MacdSlowPeriod` | `26` | MACD slow EMA |
| `MacdSignalPeriod` | `9` | MACD signal line smoothing |

#### Brain 1: Indicator Weights

| Key | Default | Description |
|-----|---------|-------------|
| `WeightRsi` | `0.30` | RSI contribution to composite score |
| `WeightBollinger` | `0.25` | Bollinger Bands contribution |
| `WeightEma` | `0.25` | EMA crossover contribution |
| `WeightMacd` | `0.20` | MACD contribution |

#### Brain 2: StockSharp Indicators

| Key | Default | Description |
|-----|---------|-------------|
| `StochPeriod` | `14` | Stochastic oscillator period |
| `StochSmoothing` | `3` | Stochastic %K/%D smoothing |
| `AdxPeriod` | `14` | ADX/DI period |
| `AtrPeriod` | `14` | ATR period |
| `WilliamsRPeriod` | `28` | Williams %R period (medium-term) |

#### Signal Thresholds

| Key | Default | Description |
|-----|---------|-------------|
| `BuyScoreThreshold` | `0.25` | Minimum score to trigger buy |
| `SellScoreThreshold` | `-0.25` | Maximum score to trigger sell |

#### Brain Weights

| Key | Default | Description |
|-----|---------|-------------|
| `WeightBrainTechnical` | `0.55` | Brain 1 weight in meta-decision |
| `WeightBrainStockSharp` | `0.45` | Brain 2 weight in meta-decision |

#### Market Regime

| Key | Default | Description |
|-----|---------|-------------|
| `UseRegimeDetection` | `true` | Enable regime-based weight adjustment |
| `RegimeLookbackCandles` | `50` | Rolling window for percentile calculations |
| `RegimeMinHistory` | `20` | Min candles before regime detection activates |

#### Logging

| Key | Default | Description |
|-----|---------|-------------|
| `LogIntervalCandles` | `4` | Log indicator state every N candles |

---

## How It Works

### Brain 1: Technical Indicators

Mean-reversion focused. Scores each indicator on a continuous **-1.0 to +1.0** scale using linear interpolation (no dead zones):

| Indicator | Bullish Signal (+) | Bearish Signal (-) |
|-----------|-------------------|-------------------|
| **RSI** | Oversold (< 30) | Overbought (> 70) |
| **Bollinger** | Price near lower band | Price near upper band |
| **EMA** | Fast crosses above slow | Fast crosses below slow |
| **MACD** | Histogram crosses positive | Histogram crosses negative |

Weighted composite score is passed to MetaDecisionEngine.

### Brain 2: StockSharp Indicators

Trend-following focused. Weighted scoring with emphasis on directional signals:

| Indicator | Weight | Role |
|-----------|--------|------|
| **ADX Direction** | 35% | Primary trend signal (+DI vs -DI) |
| **Stochastic** | 25% | Short-term overbought/oversold (14-period) |
| **OBV** | 25% | Volume confirmation |
| **Williams %R** | 15% | Medium-term overbought/oversold (28-period) |

Confidence is scaled by ADX strength (capped at 0.85) with an agreement bonus when oscillators align.

### Market Regime Detection

Uses rolling percentiles of Bollinger width and ATR, plus effective ADX (penalized for stale trends), to classify the market:

```
Indicators → Percentiles → Score each regime → Anti-flicker filter → Regime

Regime → Brain weight multipliers + threshold adjustments
```

Key features:
- **Stale trend detection** — high ADX with low BB/ATR is penalized (ADX lagging behind reality)
- **ADX slope tracking** — declining ADX weakens trend scores
- **Anti-flicker** — new regime must beat current by 0.15 margin to switch
- **Strength interpolation** — weak regime detections produce small adjustments, not binary switches

### Meta-Decision Engine

Combines brain outputs into a final decision:

```
Brain1 score × (weight × regime_mult × confidence)
  +
Brain2 score × (weight × regime_mult × confidence)
  =
Weighted final score → compare to regime-adjusted thresholds
```

- **Consensus bonus** — when all brains agree on direction, thresholds are easier to meet
- **Disagreement penalty** — when brains disagree, thresholds are raised 15%
- **Confidence gate** — average confidence must exceed 0.3 to act

### Risk Management

Layered protection:

1. **Pre-trade gates** — daily loss limit, cooldown, max positions, min order size
2. **Position-level stops** — fixed SL/TP set at entry
3. **Trailing stop** — activates at configured profit %, ratchets up, floored at SL
4. **Signal-based exit** — multi-brain sell signal can close position if PnL > MinExitPnlPercent
5. **Daily circuit breaker** — all trading halted if daily loss exceeds limit

---

## Signal Flow

```
Candle arrives (WS or REST poll)
    │
    ▼
AssetTrader.ProcessCandle()
    │
    ├── Dedup by timestamp
    ├── Update Brain 1 indicators
    ├── Update Brain 2 indicators
    ├── Update Regime Detector
    │
    ├── If position exists → CheckPositionRisk (SL/TP/trailing)
    │   └── If triggered → Sell signal
    │
    └── If no position → Generate signals
        ├── Brain 1: SignalEngine.Analyze() → TradeSignal
        ├── Brain 2: StockSharpBrain.Analyze() → StockSharpSignal
        └── MetaDecisionEngine.Decide() → MetaDecision
            │
            ├── Buy → CanOpenPosition() check → ExecuteBuy via REST
            ├── Sell → ExecuteSell via REST
            └── None → wait for next candle
```

---

## Position Lifecycle

```
BUY signal
    │
    ▼
CanOpenPosition() ← daily limit, cooldown, max positions
    │ ✅
    ▼
CalculatePositionSize() ← % of current balance
    │
    ▼
RoundVolume() ← exchange LOT_SIZE / stepSize
    │
    ▼
ExecuteOrderViaRestAsync() ← market order
    │
    ▼
ProcessFill() → OpenPosition() ← set SL, TP, persist to disk
    │
    │  ... candles arrive ...
    │
    ▼
CheckPositionRisk() every candle:
    ├── price ≤ StopLoss     → close
    ├── price ≥ TakeProfit   → close
    ├── trailing stop hit    → close
    └── multi-brain sell     → close if PnL > MinExitPnlPercent
         │
         ▼
    ClosePosition() ← calculate net PnL (minus fees), update daily PnL
         │
         ▼
    Persist state, refresh balance
```

---

## Crash Recovery

The bot persists state to `state/risk_state.json` after every position open/close and daily reset:

```json
{
  "SavedAtUtc": "2025-01-15T14:30:00Z",
  "CurrentBalance": 960.50,
  "DailyStartBalance": 1000.00,
  "DailyPnl": -39.50,
  "CurrentDay": "2025-01-15",
  "ConsecutiveLosses": 1,
  "CooldownRemaining": 0,
  "Positions": [
    {
      "Asset": "BTCUSDT",
      "EntryPrice": 42150.00,
      "Volume": 0.00950,
      "StopLoss": 41096.25,
      "TakeProfit": 44257.50,
      "TrailingStop": 0,
      "TrailingStopActive": false,
      "HighestPriceSinceEntry": 42150.00,
      "EntryTimeUnixMs": 1705312200000
    }
  ]
}
```

On restart:
1. Positions are restored and validated (invalid entries skipped with warning)
2. Historical candles are loaded to warm up indicators
3. Restored positions are checked against current prices
4. Critical warnings logged if positions are below stop loss
5. Normal operation resumes — next candle triggers risk checks

---

## Monitoring

### Log Files

Logs are written to `logs/bot_log_YYYYMMDD_HHmmss.txt` with timestamps:

```
[2025-01-15 14:30:00] 🕯️ [REST] [BTCUSDT] 2025-01-15 14:15 O=42100 H=42200 L=42050 C=42180
[2025-01-15 14:30:00] 📊 [BTCUSDT] B1: RSI=45.2 | EMA=42150/42080 | BB=41800/42100/42400
[2025-01-15 14:30:00] 📊 [BTCUSDT] B2: Stoch=55.3/52.1 ADX=28.5 W%R=-42.1 OBV=↑
[2025-01-15 14:30:00] 🌍 [BTCUSDT] Regime: TrendingBull(str=0.72, dur=8)
[2025-01-15 14:30:00] 🔍 [BTCUSDT] #142 Buy Score=0.385 (consensus=True, brains=2)
```

### Status Reports

Automatic status report every 30 minutes:

```
══════════════════════════════════════════
  📊 STATUS REPORT
  Balance: $960.50 (Initial: $1000.00)
  Total PnL: $-39.50 (-3.95%)
  Trades: 5 (W:3 L:2 WR:60.0%)
  Daily PnL: $-12.30
  Open: 1
    [BTCUSDT] Entry=42150.00 Vol=0.009500 SL=41096.25 TP=44257.50 | Now: $42280.00 | PnL: 0.31%
══════════════════════════════════════════
```

### Docker Healthcheck

The bot writes a heartbeat file every 30 minutes. Docker checks it:

```yaml
healthcheck:
  test: ["CMD", "find", "/app/state/heartbeat", "-mmin", "-60"]
  interval: 5m
  retries: 3
```

If no heartbeat for 60 minutes, Docker marks the container unhealthy and restarts it (with `restart: unless-stopped`).

### Interactive Commands

When running with a TTY (not Docker detached):

| Command | Action |
|---------|--------|
| `status` / `s` | Print current state |
| `quit` / `q` | Graceful shutdown (closes positions first) |
| `help` / `h` | Show commands |
| `Ctrl+C` | Graceful shutdown |

---

## Project Structure

```
📁 CryptoBotStockSharp/
│
├── 📁 Engine/                        # Core trading logic
│   ├── AssetTrader.cs                # Per-asset candle processing & signal routing
│   ├── MarketRegimeDetector.cs       # Regime classification (5 regimes)
│   ├── MetaDecisionEngine.cs         # Multi-brain weighted consensus
│   ├── RiskManager.cs                # Position sizing, SL/TP, daily limits, persistence
│   ├── SignalEngine.cs               # Brain 1: RSI/EMA/BB/MACD scoring
│   ├── StockSharpBrain.cs            # Brain 2: Stoch/ADX/ATR/OBV/W%R scoring
│   └── TradingBot.cs                 # Main orchestrator: connection, data, execution
│
├── 📁 Indicators/
│   └── TechnicalIndicators.cs        # RSI, EMA, Bollinger, MACD calculations
│
├── 📁 Models/
│   ├── BotConfig.cs                  # All configuration settings
│   ├── TradePosition.cs              # Position model with PnL & trailing stop
│   └── TradeSignal.cs                # Signal model (type, scores, reason)
│
├── Program.cs                        # Entry point, config loading, shutdown handling
├── appsettings.json                  # Default configuration
├── Dockerfile                        # Multi-stage build (SDK → runtime)
├── docker-compose.yml                # Container orchestration with healthcheck
├── .env                              # API keys (git-ignored)
├── .gitignore
└── .dockerignore
```

---

## Roadmap

- [x] Multi-brain consensus trading
- [x] Market regime detection with adaptive brain weights
- [x] Crash recovery with state persistence
- [x] Docker deployment with healthcheck
- [x] REST + WebSocket dual data pipeline
- [x] Continuous indicator scoring (no dead zones)
- [ ] **Brain 3: LLM integration** — Claude/GPT analysis of candle patterns and market context (config scaffolding in place)
- [ ] Backtesting engine with historical replay
- [ ] Web dashboard for real-time monitoring
- [ ] Partial position exits based on regime
- [ ] Multi-exchange support (Bybit, OKX)
- [ ] Short selling support

---

## License

This project is provided as-is for educational purposes. Use at your own risk.

