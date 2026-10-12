namespace AtmCash.Domain;

public sealed class CardDailyUsage
{
    public string CardFingerprint { get; set; } = "";
    public Currency Currency { get; set; }
    public DateOnly UtcDate { get; set; }
    public int UsedAmount { get; set; }
    public int ReservedAmount { get; set; }
}

public static class CardDailyLimitRules
{
    public static int Limit(Currency currency) => currency switch
    {
        Currency.TRY => 20_000,
        Currency.USD or Currency.EUR => 1_000,
        _ => throw new BusinessException("INVALID_CURRENCY", "Currency is not supported.")
    };

    public static DateOnly UtcDate(DateTimeOffset time) => DateOnly.FromDateTime(time.UtcDateTime);

    public static int Remaining(CardDailyUsage usage)
    {
        CashRules.Require(usage.UsedAmount >= 0 && usage.ReservedAmount >= 0
            && (long)usage.UsedAmount + usage.ReservedAmount <= Limit(usage.Currency),
            "INVALID_DAILY_USAGE", "Daily usage is outside the allowed range.");
        return Limit(usage.Currency) - usage.UsedAmount - usage.ReservedAmount;
    }

    public static void Reserve(CardDailyUsage usage, int amount)
    {
        CashRules.Require(amount > 0, "INVALID_AMOUNT", "Reserved amount must be positive.");
        CashRules.Require(amount <= Remaining(usage), "DAILY_LIMIT_EXCEEDED", "Daily withdrawal limit exceeded.", 409);
        usage.ReservedAmount += amount;
    }

    public static void Release(CardDailyUsage usage, int amount)
    {
        CashRules.Require(amount > 0 && amount <= usage.ReservedAmount,
            "INVALID_LIMIT_RELEASE", "Released amount exceeds the daily reservation.");
        usage.ReservedAmount -= amount;
    }

    public static void Confirm(CardDailyUsage usage, int reservedAmount, int withdrawnAmount)
    {
        CashRules.Require(reservedAmount > 0 && reservedAmount <= usage.ReservedAmount
            && withdrawnAmount > 0 && withdrawnAmount <= reservedAmount,
            "INVALID_LIMIT_CONFIRMATION", "Confirmed amount must be covered by the reservation.");
        usage.ReservedAmount -= reservedAmount;
        usage.UsedAmount += withdrawnAmount;
    }
}
