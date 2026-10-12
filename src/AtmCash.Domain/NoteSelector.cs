namespace AtmCash.Domain;

public sealed record NoteBreakdown(int Amount, IReadOnlyDictionary<int, int> Notes)
{
    public int NoteCount => Notes.Values.Sum();
}
public sealed record NoteSelection(NoteBreakdown? Exact, NoteBreakdown? Lower, NoteBreakdown? Upper);

/// <summary>Bounded dynamic programming over all reachable amounts; never a greedy selection.</summary>
public sealed class NoteSelector
{
    public NoteSelection Select(IReadOnlyDictionary<int, int> stock, int requested, int remainingLimit = int.MaxValue)
    {
        CashRules.Require(requested > 0, "INVALID_AMOUNT", "Withdrawal amount must be a positive integer.");
        CashRules.Require(remainingLimit >= requested, "DAILY_LIMIT_EXCEEDED", "Requested amount exceeds the remaining daily limit.", 409);
        foreach (var (denomination, count) in stock)
        {
            CashRules.ValidateNote(Currency.TRY, denomination);
            CashRules.Require(count is >= 0 and <= 400, "INVALID_STOCK", "Stock must contain between zero and 400 notes per denomination.");
        }

        // All supported values are multiples of ten; scaling reduces the DP array tenfold.
        var denominations = stock.Keys.OrderDescending().ToArray();
        var total = stock.Sum(x => x.Key * x.Value);
        var max = Math.Min(total, remainingLimit) / 10;
        var costs = Enumerable.Repeat(int.MaxValue, max + 1).ToArray();
        costs[0] = 0;
        var choices = new int[denominations.Length][];

        for (var index = 0; index < denominations.Length; index++)
        {
            var value = denominations[index] / 10;
            var count = stock[denominations[index]];
            var next = Enumerable.Repeat(int.MaxValue, max + 1).ToArray();
            var take = new int[max + 1];
            for (var amount = 0; amount <= max; amount++)
            {
                for (var quantity = 0; quantity <= count && quantity * value <= amount; quantity++)
                {
                    var previous = costs[amount - quantity * value];
                    if (previous != int.MaxValue && previous + quantity < next[amount])
                    {
                        next[amount] = previous + quantity;
                        take[amount] = quantity;
                    }
                }
            }
            costs = next;
            choices[index] = take;
        }

        NoteBreakdown Build(int units)
        {
            var amount = units * 10;
            var notes = new Dictionary<int, int>();
            for (var index = denominations.Length - 1; index >= 0; index--)
            {
                var count = choices[index][units];
                if (count > 0) notes[denominations[index]] = count;
                units -= count * (denominations[index] / 10);
            }
            return new NoteBreakdown(amount, notes);
        }

        if (requested % 10 == 0 && requested / 10 <= max && costs[requested / 10] != int.MaxValue)
            return new NoteSelection(Build(requested / 10), null, null);

        NoteBreakdown? lower = null, upper = null;
        for (var units = Math.Min(max, (requested - 1) / 10); units > 0; units--)
            if (costs[units] != int.MaxValue) { lower = Build(units); break; }
        for (var units = requested / 10 + 1; units <= max; units++)
            if (costs[units] != int.MaxValue) { upper = Build(units); break; }
        return new NoteSelection(null, lower, upper);
    }
}
