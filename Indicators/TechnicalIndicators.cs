namespace CryptoBotStockSharp.Indicators;

/// <summary>
/// Self-contained technical indicator calculations.
///
/// P1 Fixes: list trimming, _totalCandleCount, MACD signal SMA init, EMA crossover guard
/// P2 Fixes:
///   - Removed IsRangingMarket() — MarketRegimeDetector supersedes it
///   - Bollinger Bands uses sample std dev (N-1) instead of population (N)
/// </summary>
public class TechnicalIndicators
{
    // ── Configuration ──
    private readonly int _rsiPeriod;
    private readonly int _bollingerPeriod;
    private readonly double _bollingerStdDev;
    private readonly int _emaFastPeriod;
    private readonly int _emaSlowPeriod;
    private readonly int _macdFastPeriod;
    private readonly int _macdSlowPeriod;
    private readonly int _macdSignalPeriod;

    // ── Price History ──
    private readonly List<decimal> _closes = new();
    private int _totalCandleCount;

    // ── Trimming ──
    private bool _allReadyReached;
    private readonly int _maxClosesNeeded;

    // ── RSI State ──
    private decimal _avgGain;
    private decimal _avgLoss;
    public decimal Rsi { get; private set; }
    public bool RsiReady => _totalCandleCount > _rsiPeriod;

    // ── EMA State ──
    private decimal _emaFast;
    private decimal _emaSlow;
    private bool _emaFastInitialized;
    private bool _emaSlowInitialized;
    private bool _previousEmaReady;
    public decimal EmaFast => _emaFast;
    public decimal EmaSlow => _emaSlow;
    public bool EmaReady => _emaFastInitialized && _emaSlowInitialized;
    public bool PreviousEmaFastAboveSlow { get; private set; }
    public bool EmaFastAboveSlow => _emaFast > _emaSlow;

    // ── Bollinger Bands State ──
    public decimal BollingerUpper { get; private set; }
    public decimal BollingerMiddle { get; private set; }
    public decimal BollingerLower { get; private set; }
    public decimal BollingerWidth { get; private set; }
    public bool BollingerReady => _totalCandleCount >= _bollingerPeriod;

    // ── MACD State ──
    private decimal _macdEmaFast;
    private decimal _macdEmaSlow;
    private decimal _macdSignalLine;
    private bool _macdFastInit;
    private bool _macdSlowInit;
    private bool _macdSignalInit;
    private int _macdSignalCount;
    private decimal _macdSignalSum;
    private decimal _prevHistogram;
    public decimal MacdLine { get; private set; }
    public decimal MacdSignal => _macdSignalLine;
    public decimal MacdHistogram { get; private set; }
    public decimal PrevMacdHistogram => _prevHistogram;
    public bool MacdReady => _macdFastInit && _macdSlowInit && _macdSignalInit;

    // ── Overall Readiness ──
    public bool AllReady => RsiReady && EmaReady && BollingerReady && MacdReady;
    public int CandleCount => _totalCandleCount;

    public TechnicalIndicators(
        int rsiPeriod = 14,
        int bollingerPeriod = 20, double bollingerStdDev = 2.0,
        int emaFastPeriod = 9, int emaSlowPeriod = 21,
        int macdFastPeriod = 12, int macdSlowPeriod = 26, int macdSignalPeriod = 9)
    {
        _rsiPeriod = rsiPeriod;
        _bollingerPeriod = bollingerPeriod;
        _bollingerStdDev = bollingerStdDev;
        _emaFastPeriod = emaFastPeriod;
        _emaSlowPeriod = emaSlowPeriod;
        _macdFastPeriod = macdFastPeriod;
        _macdSlowPeriod = macdSlowPeriod;
        _macdSignalPeriod = macdSignalPeriod;

        _maxClosesNeeded = Math.Max(bollingerPeriod,
            Math.Max(macdSlowPeriod,
            Math.Max(emaSlowPeriod, rsiPeriod + 1))) + 5;
    }

    public void Update(decimal close)
    {
        _closes.Add(close);
        _totalCandleCount++;

        UpdateRsi(close);
        UpdateEma(close);
        UpdateBollingerBands();
        UpdateMacd(close);

        if (!_allReadyReached && AllReady)
            _allReadyReached = true;

        if (_allReadyReached)
            TrimHistory();
    }

    private void TrimHistory()
    {
        if (_closes.Count > _maxClosesNeeded)
            _closes.RemoveRange(0, _closes.Count - _maxClosesNeeded);
    }

    // ══════════════════════════════════════════
    //  RSI — Wilder's Smoothed RSI
    // ══════════════════════════════════════════
    private void UpdateRsi(decimal close)
    {
        if (_closes.Count < 2) return;

        decimal change = close - _closes[^2];
        decimal gain = change > 0 ? change : 0;
        decimal loss = change < 0 ? -change : 0;

        if (_totalCandleCount == _rsiPeriod + 1)
        {
            decimal sumGain = 0, sumLoss = 0;
            for (int i = 1; i <= _rsiPeriod; i++)
            {
                decimal diff = _closes[i] - _closes[i - 1];
                if (diff > 0) sumGain += diff;
                else sumLoss += -diff;
            }
            _avgGain = sumGain / _rsiPeriod;
            _avgLoss = sumLoss / _rsiPeriod;
        }
        else if (_totalCandleCount > _rsiPeriod + 1)
        {
            _avgGain = (_avgGain * (_rsiPeriod - 1) + gain) / _rsiPeriod;
            _avgLoss = (_avgLoss * (_rsiPeriod - 1) + loss) / _rsiPeriod;
        }
        else
        {
            return;
        }

        if (_avgLoss == 0)
            Rsi = 100;
        else
        {
            decimal rs = _avgGain / _avgLoss;
            Rsi = 100m - (100m / (1m + rs));
        }
    }

    // ══════════════════════════════════════════
    //  EMA
    // ══════════════════════════════════════════
    private void UpdateEma(decimal close)
    {
        if (_previousEmaReady)
            PreviousEmaFastAboveSlow = EmaFastAboveSlow;

        if (!_emaFastInitialized)
        {
            if (_totalCandleCount == _emaFastPeriod)
            {
                _emaFast = _closes.TakeLast(_emaFastPeriod).Average();
                _emaFastInitialized = true;
            }
        }
        else
        {
            decimal multiplier = 2m / (_emaFastPeriod + 1);
            _emaFast = (close - _emaFast) * multiplier + _emaFast;
        }

        if (!_emaSlowInitialized)
        {
            if (_totalCandleCount == _emaSlowPeriod)
            {
                _emaSlow = _closes.TakeLast(_emaSlowPeriod).Average();
                _emaSlowInitialized = true;
            }
        }
        else
        {
            decimal multiplier = 2m / (_emaSlowPeriod + 1);
            _emaSlow = (close - _emaSlow) * multiplier + _emaSlow;
        }

        if (!_previousEmaReady && EmaReady)
        {
            _previousEmaReady = true;
            PreviousEmaFastAboveSlow = EmaFastAboveSlow;
        }
    }

    // ══════════════════════════════════════════
    //  Bollinger Bands
    // ══════════════════════════════════════════

    /// <summary>
    /// P2 Fix: Uses sample standard deviation (N-1) instead of population (N).
    /// Traditional Bollinger Bands use sample std dev. With a 20-period window,
    /// population std dev underestimates volatility by ~2.5%, making bands
    /// slightly tighter and generating slightly more signals at the extremes.
    /// </summary>
    private void UpdateBollingerBands()
    {
        if (_closes.Count < _bollingerPeriod) return;

        // FIX: Index-based access instead of TakeLast().ToList()
        int start = _closes.Count - _bollingerPeriod;

        decimal sum = 0;
        for (int i = start; i < _closes.Count; i++)
            sum += _closes[i];
        decimal mean = sum / _bollingerPeriod;

        decimal sumSquares = 0;
        for (int i = start; i < _closes.Count; i++)
        {
            decimal diff = _closes[i] - mean;
            sumSquares += diff * diff;
        }

        decimal variance = _bollingerPeriod > 1
            ? sumSquares / (_bollingerPeriod - 1)
            : 0;
        decimal stdDev = (decimal)Math.Sqrt((double)variance);

        BollingerMiddle = mean;
        BollingerUpper = mean + (decimal)_bollingerStdDev * stdDev;
        BollingerLower = mean - (decimal)_bollingerStdDev * stdDev;

        decimal bandRange = BollingerUpper - BollingerLower;
        BollingerWidth = BollingerMiddle > 0 ? bandRange / BollingerMiddle : 0;
    }

    // ══════════════════════════════════════════
    //  MACD
    // ══════════════════════════════════════════
    private void UpdateMacd(decimal close)
    {
        if (!_macdFastInit)
        {
            if (_totalCandleCount == _macdFastPeriod)
            {
                _macdEmaFast = _closes.TakeLast(_macdFastPeriod).Average();
                _macdFastInit = true;
            }
        }
        else
        {
            decimal mult = 2m / (_macdFastPeriod + 1);
            _macdEmaFast = (close - _macdEmaFast) * mult + _macdEmaFast;
        }

        if (!_macdSlowInit)
        {
            if (_totalCandleCount == _macdSlowPeriod)
            {
                _macdEmaSlow = _closes.TakeLast(_macdSlowPeriod).Average();
                _macdSlowInit = true;
            }
        }
        else
        {
            decimal mult = 2m / (_macdSlowPeriod + 1);
            _macdEmaSlow = (close - _macdEmaSlow) * mult + _macdEmaSlow;
        }

        if (!_macdFastInit || !_macdSlowInit) return;

        _prevHistogram = MacdHistogram;
        MacdLine = _macdEmaFast - _macdEmaSlow;

        if (!_macdSignalInit)
        {
            _macdSignalCount++;
            _macdSignalSum += MacdLine;

            if (_macdSignalCount >= _macdSignalPeriod)
            {
                _macdSignalLine = _macdSignalSum / _macdSignalPeriod;
                _macdSignalInit = true;
            }
            else
            {
                _macdSignalLine = MacdLine;
            }
        }
        else
        {
            decimal mult = 2m / (_macdSignalPeriod + 1);
            _macdSignalLine = (MacdLine - _macdSignalLine) * mult + _macdSignalLine;
        }

        MacdHistogram = MacdLine - _macdSignalLine;
    }

    // ══════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════

    public double GetBollingerPosition(decimal price)
    {
        decimal range = BollingerUpper - BollingerLower;
        if (range == 0) return 0.5;
        return (double)((price - BollingerLower) / range);
    }

    public string GetSummary(decimal currentPrice)
    {
        return $"RSI={Rsi:F1} | EMA={EmaFast:F1}/{EmaSlow:F1} | " +
               $"BB={BollingerLower:F1}/{BollingerMiddle:F1}/{BollingerUpper:F1} " +
               $"(W={BollingerWidth:F4}) | MACD H={MacdHistogram:F4} | Price={currentPrice:F2}";
    }
}