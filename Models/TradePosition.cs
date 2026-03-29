namespace CryptoBotStockSharp.Models;

/// <summary>
/// Represents an open trading position.
///
/// P2 Fixes:
///   - Constructor validates EntryPrice > 0 and Volume > 0
///   - UpdateTrailingStop floors trailing stop at StopLoss
/// </summary>
public class TradePosition
{
    public string Asset { get; init; } = "";
    public decimal EntryPrice { get; init; }
    public decimal Volume { get; init; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal TrailingStop { get; set; }
    public bool TrailingStopActive { get; set; }
    public decimal HighestPriceSinceEntry { get; set; }
    public DateTimeOffset EntryTime { get; set; }
    public long OrderTransactionId { get; set; }

    /// <summary>
    /// Validated constructor — enforces non-zero entry price and volume.
    /// </summary>
    public TradePosition(string asset, decimal entryPrice, decimal volume,
                          decimal stopLoss, decimal takeProfit,
                          DateTimeOffset? entryTime = null)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ArgumentException("Asset cannot be empty", nameof(asset));
        if (entryPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(entryPrice),
                $"Entry price must be > 0, got {entryPrice}");
        if (volume <= 0)
            throw new ArgumentOutOfRangeException(nameof(volume),
                $"Volume must be > 0, got {volume}");

        Asset = asset;
        EntryPrice = entryPrice;
        Volume = volume;
        StopLoss = stopLoss;
        TakeProfit = takeProfit;
        TrailingStop = 0;
        TrailingStopActive = false;
        HighestPriceSinceEntry = entryPrice;
        EntryTime = entryTime ?? DateTimeOffset.UtcNow;  // FIX: avoid redundant syscall
    }

    /// <summary>
    /// Parameterless constructor for deserialization only.
    /// </summary>
    public TradePosition() { }

    public decimal GetUnrealizedPnl(decimal currentPrice)
    {
        return (currentPrice - EntryPrice) * Volume;
    }

    public double GetUnrealizedPnlPercent(decimal currentPrice)
    {
        if (EntryPrice == 0) return 0;
        return (double)((currentPrice - EntryPrice) / EntryPrice) * 100.0;
    }

    /// <summary>
    /// P2 Fix: Trailing stop is floored at StopLoss.
    ///
    /// Previously, with certain config combinations (e.g., TrailingStopDistance
    /// larger than TrailingStopActivation), the trailing stop could be set
    /// below the fixed stop loss — making the fixed SL the binding constraint
    /// and the trailing stop meaningless.
    ///
    /// Now the trailing stop is always >= StopLoss, ensuring it only
    /// provides tighter protection, never looser.
    /// </summary>
    public void UpdateTrailingStop(decimal currentPrice, double trailingDistancePercent)
    {
        if (currentPrice > HighestPriceSinceEntry)
        {
            HighestPriceSinceEntry = currentPrice;
            decimal newTrailing = HighestPriceSinceEntry *
                (1m - (decimal)trailingDistancePercent / 100m);

            // P2 Fix: Never let trailing stop fall below fixed stop loss
            TrailingStop = Math.Max(newTrailing, StopLoss);
        }
    }

    public override string ToString()
    {
        return $"[{Asset}] Entry={EntryPrice:F2} Vol={Volume:F6} SL={StopLoss:F2} " +
               $"TP={TakeProfit:F2} Trail={TrailingStop:F2}";
    }
}