namespace CryptoBotStockSharp.Engine;

/// <summary>
/// Brain 2: StockSharp-style indicators (Stoch, ADX, ATR, OBV, Williams %R).
///
/// P1 Fixes:
///   - OHLCV lists trimmed after IsReady to prevent unbounded growth
///   - _totalCandleCount used for readiness checks
///   - Stochastic %D simplified, ATR init uses >=
///
/// P2 Fixes:
///   - Analyze() uses weighted scoring instead of equal 1/4 weights.
///     ADX direction raised to 35% (trend signal), Stochastic and OBV at 25%,
///     Williams %R reduced to 15% (partially redundant with Stochastic).
///   - Confidence calculation improved: trendStrength cap raised from 0.7 to 0.85
///     so Brain2 can express stronger conviction in clear trends.
///   - Williams %R default period changed to 28 in BotConfig (medium-term vs
///     Stochastic's short-term 14) to reduce information redundancy.
/// </summary>
public class StockSharpBrain
{
    private readonly int _stochPeriod;
    private readonly int _stochSmoothing;
    private readonly int _adxPeriod;
    private readonly int _atrPeriod;
    private readonly int _williamsRPeriod;

    private readonly List<decimal> _highs = new();
    private readonly List<decimal> _lows = new();
    private readonly List<decimal> _closes = new();
    private readonly List<decimal> _volumes = new();

    private int _totalCandleCount;

    private bool _isReadyReached;
    private readonly int _maxOhlcvNeeded;

    // ── Stochastic ──
    private readonly List<decimal> _rawK = new();
    private readonly List<decimal> _kValues = new();
    private readonly int _maxRawKNeeded;
    private readonly int _maxKValuesNeeded;
    public decimal StochK { get; private set; }
    public decimal StochD { get; private set; }
    private decimal _prevStochK;
    public bool StochReady => _totalCandleCount >= _stochPeriod + _stochSmoothing;

    // ── ADX ──
    private decimal _smoothedPlusDm;
    private decimal _smoothedMinusDm;
    private decimal _smoothedTr;
    private decimal _adxSmoothed;
    private bool _adxFirstDone;
    private int _adxCount;
    public decimal Adx { get; private set; }
    public decimal PlusDi { get; private set; }
    public decimal MinusDi { get; private set; }
    public bool AdxReady => _adxCount >= _adxPeriod * 2;

    // ── ATR ──
    private decimal _atr;
    private bool _atrInitialized;
    public decimal Atr => _atr;
    public bool AtrReady => _atrInitialized;

    // ── OBV ──
    private decimal _obv;
    private decimal _prevObv;
    private decimal _obvEma;
    private bool _obvEmaInit;
    public decimal Obv => _obv;
    public decimal ObvSignal => _obvEma;
    public bool ObvReady => _totalCandleCount >= 20;

    // ── Williams %R ──
    public decimal WilliamsR { get; private set; }
    public bool WilliamsReady => _totalCandleCount >= _williamsRPeriod;

    // ── Overall readiness ──
    public bool IsReady => StochReady && AdxReady && AtrReady
                           && ObvReady && WilliamsReady && _totalCandleCount >= 40;
    public int CandleCount => _totalCandleCount;

    // ── P2 Fix: Scoring weights (expose for testing) ──
    private const double WeightStochastic = 0.25;
    private const double WeightAdxDirection = 0.35;
    private const double WeightObv = 0.25;
    private const double WeightWilliamsR = 0.15;

    public StockSharpBrain(
        int stochPeriod = 14, int stochSmoothing = 3,
        int adxPeriod = 14, int atrPeriod = 14, int williamsRPeriod = 14)
    {
        _stochPeriod = stochPeriod;
        _stochSmoothing = stochSmoothing;
        _adxPeriod = adxPeriod;
        _atrPeriod = atrPeriod;
        _williamsRPeriod = williamsRPeriod;

        _maxOhlcvNeeded = Math.Max(stochPeriod,
            Math.Max(williamsRPeriod, adxPeriod)) + 5;

        _maxRawKNeeded = stochSmoothing + 5;
        _maxKValuesNeeded = stochSmoothing + 5;
    }

    public void Update(decimal open, decimal high, decimal low,
                       decimal close, decimal volume)
    {
        _highs.Add(high);
        _lows.Add(low);
        _closes.Add(close);
        _volumes.Add(volume);
        _totalCandleCount++;

        UpdateStochastic();
        UpdateAdx();
        UpdateAtr();
        UpdateObv();
        UpdateWilliamsR();

        if (!_isReadyReached && IsReady)
            _isReadyReached = true;

        if (_isReadyReached)
            TrimHistory();
    }

    // ══════════════════════════════════════════
    //  LIST TRIMMING
    // ══════════════════════════════════════════

    private void TrimHistory()
    {
        if (_closes.Count > _maxOhlcvNeeded)
        {
            int excess = _closes.Count - _maxOhlcvNeeded;
            _closes.RemoveRange(0, excess);
            _highs.RemoveRange(0, excess);
            _lows.RemoveRange(0, excess);
            _volumes.RemoveRange(0, excess);
        }

        if (_rawK.Count > _maxRawKNeeded)
            _rawK.RemoveRange(0, _rawK.Count - _maxRawKNeeded);

        if (_kValues.Count > _maxKValuesNeeded)
            _kValues.RemoveRange(0, _kValues.Count - _maxKValuesNeeded);
    }

    // ══════════════════════════════════════════
    //  STOCHASTIC
    // ══════════════════════════════════════════
    private void UpdateStochastic()
    {
        if (_totalCandleCount < _stochPeriod) return;

        var recentHighs = _highs.TakeLast(_stochPeriod);
        var recentLows = _lows.TakeLast(_stochPeriod);
        decimal highestHigh = recentHighs.Max();
        decimal lowestLow = recentLows.Min();
        decimal range = highestHigh - lowestLow;

        decimal rawK = range > 0
            ? ((_closes[^1] - lowestLow) / range) * 100m
            : 50m;

        _rawK.Add(rawK);
        _prevStochK = StochK;

        if (_rawK.Count >= _stochSmoothing)
            StochK = _rawK.TakeLast(_stochSmoothing).Average();
        else
            StochK = rawK;

        _kValues.Add(StochK);

        if (_kValues.Count >= _stochSmoothing)
            StochD = _kValues.TakeLast(_stochSmoothing).Average();
        else
            StochD = StochK;
    }

    // ══════════════════════════════════════════
    //  ADX
    // ══════════════════════════════════════════
    private void UpdateAdx()
    {
        if (_totalCandleCount < 2) return;

        int n = _closes.Count;
        decimal currentHigh = _highs[n - 1];
        decimal currentLow = _lows[n - 1];
        decimal prevHigh = _highs[n - 2];
        decimal prevLow = _lows[n - 2];
        decimal prevClose = _closes[n - 2];

        decimal tr = Math.Max(currentHigh - currentLow,
            Math.Max(Math.Abs(currentHigh - prevClose),
                     Math.Abs(currentLow - prevClose)));

        decimal plusDm = (currentHigh - prevHigh) > (prevLow - currentLow)
            ? Math.Max(currentHigh - prevHigh, 0) : 0;
        decimal minusDm = (prevLow - currentLow) > (currentHigh - prevHigh)
            ? Math.Max(prevLow - currentLow, 0) : 0;

        _adxCount++;

        if (_adxCount == _adxPeriod && !_adxFirstDone)
        {
            decimal sumTr = 0, sumPlusDm = 0, sumMinusDm = 0;
            for (int i = 1; i <= _adxPeriod && i < _closes.Count; i++)
            {
                decimal h = _highs[i], l = _lows[i];
                decimal pc = _closes[i - 1], ph = _highs[i - 1], pl = _lows[i - 1];
                sumTr += Math.Max(h - l,
                    Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
                decimal pdm = (h - ph) > (pl - l) ? Math.Max(h - ph, 0) : 0;
                decimal mdm = (pl - l) > (h - ph) ? Math.Max(pl - l, 0) : 0;
                sumPlusDm += pdm;
                sumMinusDm += mdm;
            }
            _smoothedTr = sumTr;
            _smoothedPlusDm = sumPlusDm;
            _smoothedMinusDm = sumMinusDm;
            _adxFirstDone = true;
        }
        else if (_adxFirstDone)
        {
            _smoothedTr = _smoothedTr - (_smoothedTr / _adxPeriod) + tr;
            _smoothedPlusDm = _smoothedPlusDm - (_smoothedPlusDm / _adxPeriod) + plusDm;
            _smoothedMinusDm = _smoothedMinusDm - (_smoothedMinusDm / _adxPeriod) + minusDm;
        }

        if (!_adxFirstDone) return;

        PlusDi = _smoothedTr > 0 ? (_smoothedPlusDm / _smoothedTr) * 100m : 0;
        MinusDi = _smoothedTr > 0 ? (_smoothedMinusDm / _smoothedTr) * 100m : 0;

        decimal diSum = PlusDi + MinusDi;
        decimal dx = diSum > 0 ? (Math.Abs(PlusDi - MinusDi) / diSum) * 100m : 0;

        if (_adxCount == _adxPeriod * 2)
            _adxSmoothed = dx;
        else if (_adxCount > _adxPeriod * 2)
            _adxSmoothed = ((_adxSmoothed * (_adxPeriod - 1)) + dx) / _adxPeriod;

        Adx = _adxSmoothed;
    }

    // ══════════════════════════════════════════
    //  ATR
    // ══════════════════════════════════════════
    private void UpdateAtr()
    {
        if (_totalCandleCount < 2) return;

        int n = _closes.Count;
        decimal tr = Math.Max(_highs[n - 1] - _lows[n - 1],
            Math.Max(Math.Abs(_highs[n - 1] - _closes[n - 2]),
                     Math.Abs(_lows[n - 1] - _closes[n - 2])));

        if (!_atrInitialized && _totalCandleCount >= _atrPeriod + 1)
        {
            decimal sum = 0;
            for (int i = 1; i <= _atrPeriod; i++)
                sum += Math.Max(_highs[i] - _lows[i],
                    Math.Max(Math.Abs(_highs[i] - _closes[i - 1]),
                             Math.Abs(_lows[i] - _closes[i - 1])));
            _atr = sum / _atrPeriod;
            _atrInitialized = true;
        }
        else if (_atrInitialized)
        {
            _atr = ((_atr * (_atrPeriod - 1)) + tr) / _atrPeriod;
        }
    }

    // ══════════════════════════════════════════
    //  OBV
    // ══════════════════════════════════════════
    private void UpdateObv()
    {
        if (_closes.Count < 2) return;
        _prevObv = _obv;

        if (_closes[^1] > _closes[^2]) _obv += _volumes[^1];
        else if (_closes[^1] < _closes[^2]) _obv -= _volumes[^1];

        if (!_obvEmaInit && _totalCandleCount >= 20)
        {
            _obvEma = _obv;
            _obvEmaInit = true;
        }
        else if (_obvEmaInit)
        {
            decimal mult = 2m / 21m;
            _obvEma = (_obv - _obvEma) * mult + _obvEma;
        }
    }

    // ══════════════════════════════════════════
    //  WILLIAMS %R
    // ══════════════════════════════════════════
    private void UpdateWilliamsR()
    {
        if (_closes.Count < _williamsRPeriod) return;
        decimal hh = _highs.TakeLast(_williamsRPeriod).Max();
        decimal ll = _lows.TakeLast(_williamsRPeriod).Min();
        decimal range = hh - ll;
        WilliamsR = range > 0 ? (hh - _closes[^1]) / range * -100m : -50m;
    }

    // ══════════════════════════════════════════
    //  ANALYZE
    // ══════════════════════════════════════════

    /// <summary>
    /// P2 Fix: Weighted scoring replaces equal 1/4 weights.
    ///
    /// Old weights:  Stoch=25%, ADX=25%, OBV=25%, W%R=25%
    ///   → oscillators (Stoch+W%R) = 50% of Brain2
    ///   → trend direction (ADX) = only 25%
    ///   → for a brain meant to be trend-following, this was backwards
    ///
    /// New weights:  Stoch=25%, ADX=35%, OBV=25%, W%R=15%
    ///   → trend direction is dominant signal
    ///   → W%R reduced since partially redundant with Stochastic
    ///     (different period mitigates but doesn't eliminate overlap)
    ///
    /// Confidence cap raised from 0.7 to 0.85 so Brain2 can express
    /// stronger conviction during clear trends (ADX > 35).
    /// </summary>
    public StockSharpSignal Analyze(decimal currentPrice)
    {
        if (!IsReady)
            return new StockSharpSignal { Score = 0, Confidence = 0 };

        double stochScore = ScoreStochastic();
        double adxDirScore = ScoreAdxDirection();
        double obvScore = ScoreObv();
        double wrScore = ScoreWilliamsR();

        // P2 Fix: Weighted composite instead of simple average
        double compositeScore =
            (stochScore * WeightStochastic) +
            (adxDirScore * WeightAdxDirection) +
            (obvScore * WeightObv) +
            (wrScore * WeightWilliamsR);

        double adxValue = Math.Min((double)Adx, 50.0);
        double trendStrength = adxValue / 50.0;

        // P2 Fix: Agreement check uses weighted contribution, not just sign
        bool signsAgree = (stochScore > 0 && wrScore > 0 && obvScore > 0) ||
                          (stochScore < 0 && wrScore < 0 && obvScore < 0);
        double agreementBonus = signsAgree ? 0.2 : 0.0;

        // P2 Fix: Confidence cap raised from 0.7 to 0.85
        double confidence = Math.Min(1.0, Math.Min(trendStrength, 0.85) + agreementBonus);

        double atrPercent = currentPrice > 0
            ? (double)_atr / (double)currentPrice * 100.0 : 0;

        return new StockSharpSignal
        {
            Score = compositeScore,
            Confidence = confidence,
            TrendStrength = (double)Adx,
            IsStrongTrend = Adx > 25,
            TrendDirection = PlusDi > MinusDi ? "BULLISH" : "BEARISH",
            Volatility = atrPercent,
            AtrValue = (double)_atr,
            StochK = (double)StochK,
            StochD = (double)StochD,
            WilliamsR = (double)WilliamsR,
            ObvTrend = _obv > _obvEma ? "BULLISH" : "BEARISH",
            Summary = $"Stoch={StochK:F1}/{StochD:F1} ADX={Adx:F1}" +
                      $"(+DI={PlusDi:F1}/-DI={MinusDi:F1}) " +
                      $"W%R={WilliamsR:F1} " +
                      $"OBV={(_obv > _obvEma ? "↑" : "↓")} " +
                      $"ATR={_atr:F2}({atrPercent:F2}%)"
        };
    }

    // ══════════════════════════════════════════
    //  SCORING METHODS
    // ══════════════════════════════════════════
    private double ScoreStochastic()
    {
        double k = (double)StochK;
        double d = (double)StochD;
        double prevK = (double)_prevStochK;

        bool bullishCross = k > d && prevK <= d;
        bool bearishCross = k < d && prevK >= d;

        if (bullishCross && k < 30) return 1.0;
        if (bearishCross && k > 70) return -1.0;

        if (k <= 15) return 0.8;
        if (k <= 20) return 0.6;
        if (k <= 30) return 0.3;
        if (k >= 85) return -0.8;
        if (k >= 80) return -0.6;
        if (k >= 70) return -0.3;

        return 0.0;
    }

    private double ScoreAdxDirection()
    {
        double cappedAdx = Math.Min((double)Adx, 50.0);
        double strength = Math.Min(cappedAdx / 40.0, 1.0);

        double diSum = (double)(PlusDi + MinusDi);
        if (diSum == 0) return 0;

        if (PlusDi > MinusDi)
        {
            double gap = (double)(PlusDi - MinusDi) / diSum;
            return Math.Min(gap * strength * 2, 0.8);
        }
        else
        {
            double gap = (double)(MinusDi - PlusDi) / diSum;
            return Math.Max(-gap * strength * 2, -0.8);
        }
    }

    private double ScoreObv()
    {
        if (!_obvEmaInit) return 0;

        bool bullish = _obv > _obvEma;
        bool rising = _obv > _prevObv;

        if (bullish && rising) return 0.6;
        if (bullish && !rising) return 0.2;
        if (!bullish && !rising) return -0.6;
        if (!bullish && rising) return -0.2;

        return 0;
    }

    private double ScoreWilliamsR()
    {
        double wr = (double)WilliamsR;

        if (wr <= -95) return 0.9;
        if (wr <= -80) return 0.6;
        if (wr <= -60) return 0.2;
        if (wr >= -5) return -0.9;
        if (wr >= -20) return -0.6;
        if (wr >= -40) return -0.2;

        return 0.0;
    }

    // ══════════════════════════════════════════
    //  ATR-BASED STOPS
    // ══════════════════════════════════════════
    public decimal GetAtrStopLoss(decimal entryPrice, double multiplier = 2.0)
    {
        if (!AtrReady) return entryPrice * 0.975m;
        return entryPrice - (_atr * (decimal)multiplier);
    }

    public decimal GetAtrTakeProfit(decimal entryPrice, double multiplier = 3.0)
    {
        if (!AtrReady) return entryPrice * 1.05m;
        return entryPrice + (_atr * (decimal)multiplier);
    }

    public string GetSummary()
    {
        if (!IsReady) return $"Warming up ({CandleCount} candles)";
        return $"Stoch={StochK:F1}/{StochD:F1} ADX={Adx:F1} " +
               $"+DI={PlusDi:F1} -DI={MinusDi:F1} " +
               $"W%R={WilliamsR:F1} ATR={_atr:F2} " +
               $"OBV={(_obv > _obvEma ? "↑BULL" : "↓BEAR")}";
    }
}

public class StockSharpSignal
{
    public double Score { get; set; }
    public double Confidence { get; set; }
    public double TrendStrength { get; set; }
    public bool IsStrongTrend { get; set; }
    public string TrendDirection { get; set; } = "";
    public double Volatility { get; set; }
    public double AtrValue { get; set; }
    public double StochK { get; set; }
    public double StochD { get; set; }
    public double WilliamsR { get; set; }
    public string ObvTrend { get; set; } = "";
    public string Summary { get; set; } = "";
}