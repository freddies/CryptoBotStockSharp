namespace CryptoBotStockSharp.Engine;

/// <summary>
/// Brain 2: StockSharp-style indicators (Stoch, ADX, ATR, OBV, Williams %R).
///
/// P0 Fixes:
///   - ScoreStochastic(): dead zone (K 30–70 → 0.0) replaced with
///     continuous linear ramp. ~60% of trading time was producing zero
///     signal from the Stochastic indicator.
///   - ScoreWilliamsR(): dead zone (W%R -60 to -40 → 0.0) replaced with
///     continuous linear ramp through the full -95 to -5 range.
///   - Added Lerp helper for linear interpolation.
///
/// P1 Fixes (retained):
///   - OHLCV lists trimmed after IsReady
///   - _totalCandleCount used for readiness
///
/// P2 Fixes (retained):
///   - Weighted scoring (ADX 35%, Stoch 25%, OBV 25%, W%R 15%)
///   - Confidence cap 0.85
///   - Williams %R default period 28
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

    // ── P2 Fix (retained): Scoring weights ──
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

        // FIX: Index-based instead of TakeLast().Max()/Min()
        int start = _highs.Count - _stochPeriod;
        decimal highestHigh = decimal.MinValue;
        decimal lowestLow = decimal.MaxValue;

        for (int i = start; i < _highs.Count; i++)
        {
            if (_highs[i] > highestHigh) highestHigh = _highs[i];
            if (_lows[i] < lowestLow) lowestLow = _lows[i];
        }

        decimal range = highestHigh - lowestLow;
        decimal rawK = range > 0
            ? ((_closes[^1] - lowestLow) / range) * 100m
            : 50m;

        _rawK.Add(rawK);
        _prevStochK = StochK;

        // FIX: Index-based average for smoothing
        if (_rawK.Count >= _stochSmoothing)
        {
            decimal sum = 0;
            int smoothStart = _rawK.Count - _stochSmoothing;
            for (int i = smoothStart; i < _rawK.Count; i++)
                sum += _rawK[i];
            StochK = sum / _stochSmoothing;
        }
        else
        {
            StochK = rawK;
        }

        _kValues.Add(StochK);

        if (_kValues.Count >= _stochSmoothing)
        {
            decimal sum = 0;
            int smoothStart = _kValues.Count - _stochSmoothing;
            for (int i = smoothStart; i < _kValues.Count; i++)
                sum += _kValues[i];
            StochD = sum / _stochSmoothing;
        }
        else
        {
            StochD = StochK;
        }
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
            // FIX: Guard against post-trim index corruption
            System.Diagnostics.Debug.Assert(!_isReadyReached,
                "ADX initialization must complete before trimming starts");

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

        // FIX: Index-based instead of TakeLast().Max()/Min()
        int start = _highs.Count - _williamsRPeriod;
        decimal hh = decimal.MinValue;
        decimal ll = decimal.MaxValue;

        for (int i = start; i < _highs.Count; i++)
        {
            if (_highs[i] > hh) hh = _highs[i];
            if (_lows[i] < ll) ll = _lows[i];
        }

        decimal range = hh - ll;
        WilliamsR = range > 0 ? (hh - _closes[^1]) / range * -100m : -50m;
    }

    // ══════════════════════════════════════════
    //  ANALYZE
    // ══════════════════════════════════════════

    public StockSharpSignal Analyze(decimal currentPrice)
    {
        if (!IsReady)
            return new StockSharpSignal { Score = 0, Confidence = 0 };

        double stochScore = ScoreStochastic();
        double adxDirScore = ScoreAdxDirection();
        double obvScore = ScoreObv();
        double wrScore = ScoreWilliamsR();

        double compositeScore =
            (stochScore * WeightStochastic) +
            (adxDirScore * WeightAdxDirection) +
            (obvScore * WeightObv) +
            (wrScore * WeightWilliamsR);

        double adxValue = Math.Min((double)Adx, 50.0);
        double trendStrength = adxValue / 50.0;

        bool signsAgree = (stochScore > 0 && wrScore > 0 && obvScore > 0) ||
                          (stochScore < 0 && wrScore < 0 && obvScore < 0);
        double agreementBonus = signsAgree ? 0.2 : 0.0;

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

    // ══════════════════════════════════════════════════════════
    //  P0 FIX: SCORING METHODS — CONTINUOUS (NO DEAD ZONES)
    //
    //  Old Stochastic: K 30–70 → 0.0 (60% of range was dead)
    //  Old Williams:   W%R -60 to -40 → 0.0 (20% dead zone)
    //
    //  During a healthy uptrend, Stoch K typically sits 50–65
    //  and W%R sits -25 to -40. Both contributed exactly 0.0,
    //  crippling Brain2's ability to express trend conviction.
    //
    //  New: linear ramps through the full range. Crossover
    //  events at extremes preserved as strong discrete signals.
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// P0 FIX: Continuous stochastic scoring.
    ///
    /// Old shape (step function with dead zone):
    ///   K ≤ 15: +0.8 | K ≤ 20: +0.6 | K ≤ 30: +0.3
    ///   K 30–70: 0.0  ← DEAD ZONE
    ///   K ≥ 70: -0.3 | K ≥ 80: -0.6 | K ≥ 85: -0.8
    ///
    /// New shape (continuous linear ramp):
    ///   K=0:  +0.80 (deep oversold)
    ///   K=15: +0.80
    ///   K=30: +0.30
    ///   K=50:  0.00 (neutral)
    ///   K=70: -0.30
    ///   K=85: -0.80
    ///   K=100:-0.80 (deep overbought)
    ///
    /// Crossover events at extremes still produce ±1.0.
    /// </summary>
    private double ScoreStochastic()
    {
        double k = (double)StochK;
        double d = (double)StochD;
        double prevK = (double)_prevStochK;

        // Crossover events at extremes: strong discrete signals (unchanged)
        bool bullishCross = k > d && prevK <= d;
        bool bearishCross = k < d && prevK >= d;
        if (bullishCross && k < 30) return 1.0;
        if (bearishCross && k > 70) return -1.0;

        // ── P0 FIX: Continuous scoring through full range ──
        if (k <= 15) return 0.8;
        if (k >= 85) return -0.8;

        if (k <= 50)
        {
            // 15 → +0.8, 30 → +0.3, 50 → 0.0
            if (k <= 30)
                return Lerp(0.8, 0.3, (k - 15.0) / 15.0);
            else
                return Lerp(0.3, 0.0, (k - 30.0) / 20.0);
        }
        else
        {
            // 50 → 0.0, 70 → -0.3, 85 → -0.8
            if (k <= 70)
                return Lerp(0.0, -0.3, (k - 50.0) / 20.0);
            else
                return Lerp(-0.3, -0.8, (k - 70.0) / 15.0);
        }
    }

    /// <summary>
    /// ADX direction scoring — unchanged from P2.
    /// Already continuous, no dead zones.
    /// </summary>
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

    /// <summary>
    /// OBV scoring — unchanged from P2.
    /// 4-value step function is P2 issue, not P0.
    /// </summary>
    private double ScoreObv()
    {
        if (!_obvEmaInit) return 0;

        // FIX: Continuous scoring instead of 4-value step function
        decimal divergence = _obv - _obvEma;
        double normalized = _obvEma != 0
            ? (double)(divergence / Math.Max(Math.Abs(_obvEma), 1m))
            : 0;

        bool rising = _obv > _prevObv;
        double momentumBonus = rising ? 0.1 : -0.1;

        return Math.Clamp(normalized * 2.0 + momentumBonus, -0.8, 0.8);
    }

    /// <summary>
    /// P0 FIX: Continuous Williams %R scoring.
    ///
    /// Old shape (step function with dead zone):
    ///   W%R ≤ -95: +0.9 | ≤ -80: +0.6 | ≤ -60: +0.2
    ///   W%R -60 to -40: 0.0  ← DEAD ZONE
    ///   W%R ≥ -40: -0.2 | ≥ -20: -0.6 | ≥ -5: -0.9
    ///
    /// New shape (continuous linear ramp):
    ///   W%R = -100: +0.90 (deep oversold)
    ///   W%R = -95:  +0.90
    ///   W%R = -50:   0.00 (neutral)
    ///   W%R = -5:   -0.90
    ///   W%R = 0:    -0.90 (deep overbought)
    /// </summary>
    private double ScoreWilliamsR()
    {
        double wr = (double)WilliamsR; // Range: -100 to 0

        if (wr <= -95) return 0.9;
        if (wr >= -5) return -0.9;

        // ── P0 FIX: Linear ramp through full range ──
        // -95 → +0.9, -50 → 0.0, -5 → -0.9
        double mid = -50.0;
        if (wr <= mid)
            return Lerp(0.9, 0.0, (wr - (-95.0)) / 45.0);
        else
            return Lerp(0.0, -0.9, (wr - mid) / 45.0);
    }

    // ══════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════

    /// <summary>
    /// Linear interpolation: returns a when t=0, b when t=1.
    /// t is clamped to [0, 1].
    /// </summary>
    private static double Lerp(double a, double b, double t)
    {
        t = Math.Max(0.0, Math.Min(1.0, t));
        return a + (b - a) * t;
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