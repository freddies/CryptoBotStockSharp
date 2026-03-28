namespace CryptoBotStockSharp.Models;

public enum SignalType
{
    None,
    Buy,
    Sell
}

public class TradeSignal
{
    public SignalType Type { get; set; } = SignalType.None;
    public double CompositeScore { get; set; }
    public double RsiScore { get; set; }
    public double BollingerScore { get; set; }
    public double EmaScore { get; set; }
    public double MacdScore { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }

    public override string ToString()
    {
        return $"Signal={Type} Score={CompositeScore:F3} " +
               $"[RSI={RsiScore:F2} BB={BollingerScore:F2} " +
               $"EMA={EmaScore:F2} MACD={MacdScore:F2}] {Reason}";
    }
}