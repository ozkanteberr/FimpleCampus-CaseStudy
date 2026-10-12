using AtmCash.Domain;

namespace AtmCash.Tests;

public sealed class NoteSelectorTests
{
    private readonly NoteSelector selector = new();

    public static IEnumerable<object?[]> AcceptanceCases => new object?[][]
    {
        Case("B3-1 TRY", [100,50,20], [0,3,10], 160, 160, [50,20], [2,3], null, [], [], null, [], []),
        Case("B3-2 TRY", [50,20], [1,2], 80, null, [], [], 70, [50,20], [1,1], 90, [50,20], [1,2]),
        Case("B3-3 TRY", [100,20], [5,10], 130, null, [], [], 120, [100,20], [1,1], 140, [100,20], [1,2]),
        Case("B3-4 USD", [100], [2], 250, null, [], [], 200, [100], [2], null, [], []),
        Case("B3-5 TRY", [500,100,20], [1,0,5], 600, 600, [500,20], [1,5], null, [], [], null, [], []),
        Case("B3-6 TRY", [100,50,20], [1,4,5], 200, 200, [100,50], [1,2], null, [], [], null, [], []),
        Case("B3-7 TRY", [500,100,20], [1,3,20], 1000, 1000, [500,100,20], [1,3,10], null, [], [], null, [], []),
        Case("B3-8 TRY", [100,50,20], [3,1,1], 110, null, [], [], 100, [100], [1], 120, [100,20], [1,1])
    };

    [Theory]
    [MemberData(nameof(AcceptanceCases))]
    public void Pdf_acceptance_case(string name, int[] values, int[] counts, int requested,
        int? exact, int[] exactValues, int[] exactCounts, int? lower, int[] lowerValues, int[] lowerCounts,
        int? upper, int[] upperValues, int[] upperCounts)
    {
        Assert.NotEmpty(name);
        var result = selector.Select(Stock(values, counts), requested);
        AssertBreakdown(result.Exact, exact, Stock(exactValues, exactCounts));
        AssertBreakdown(result.Lower, lower, Stock(lowerValues, lowerCounts));
        AssertBreakdown(result.Upper, upper, Stock(upperValues, upperCounts));
    }

    [Fact]
    public void Upper_alternative_cannot_exceed_remaining_daily_limit()
    {
        var result = selector.Select(new Dictionary<int, int> { [50] = 1, [20] = 2 }, 80, 85);
        Assert.Equal(70, result.Lower?.Amount);
        Assert.Null(result.Upper);
    }

    [Fact]
    public void Empty_stock_has_no_alternatives()
    {
        var result = selector.Select(new Dictionary<int, int>(), 100);
        Assert.Null(result.Exact);
        Assert.Null(result.Lower);
        Assert.Null(result.Upper);
    }

    [Fact]
    public void Non_multiple_of_ten_gets_correct_neighbours()
    {
        var result = selector.Select(new Dictionary<int, int> { [20] = 3 }, 39);
        Assert.Equal(20, result.Lower?.Amount);
        Assert.Equal(40, result.Upper?.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_amount_is_rejected(int amount) => Assert.Equal("INVALID_AMOUNT",
        Assert.Throws<BusinessException>(() => selector.Select(new Dictionary<int, int>(), amount)).Code);

    [Fact]
    public void Request_above_limit_is_rejected() => Assert.Equal("DAILY_LIMIT_EXCEEDED",
        Assert.Throws<BusinessException>(() => selector.Select(new Dictionary<int, int> { [100] = 5 }, 200, 100)).Code);

    [Fact]
    public void Matches_independent_exhaustive_search_for_small_stocks()
    {
        var random = new Random(712);
        for (var sample = 0; sample < 30; sample++)
        {
            var stock = CashRules.Denominations.ToDictionary(x => x, _ => random.Next(4));
            var oracle = new Dictionary<int, int>();
            for (var a = 0; a <= stock[20]; a++)
            for (var b = 0; b <= stock[50]; b++)
            for (var c = 0; c <= stock[100]; c++)
            for (var d = 0; d <= stock[500]; d++)
            {
                var amount = 20 * a + 50 * b + 100 * c + 500 * d;
                var count = a + b + c + d;
                if (!oracle.TryGetValue(amount, out var previous) || count < previous) oracle[amount] = count;
            }
            for (var trial = 0; trial < 12; trial++)
            {
                var requested = random.Next(1, 2001);
                var result = selector.Select(stock, requested);
                if (oracle.ContainsKey(requested))
                {
                    Assert.NotNull(result.Exact);
                    Assert.Equal(oracle[requested], result.Exact.NoteCount);
                }
                else
                {
                    Assert.Null(result.Exact);
                    var lower = oracle.Keys.Where(x => x > 0 && x < requested).Select(x => (int?)x).Max();
                    var upper = oracle.Keys.Where(x => x > requested).Select(x => (int?)x).Min();
                    Assert.Equal(lower, result.Lower?.Amount);
                    Assert.Equal(upper, result.Upper?.Amount);
                }
                foreach (var breakdown in new[] { result.Exact, result.Lower, result.Upper }.OfType<NoteBreakdown>())
                {
                    Assert.Equal(oracle[breakdown.Amount], breakdown.NoteCount);
                    Assert.Equal(breakdown.Amount, breakdown.Notes.Sum(x => x.Key * x.Value));
                    Assert.All(breakdown.Notes, x => Assert.InRange(x.Value, 1, stock[x.Key]));
                }
            }
        }
    }

    private static object?[] Case(string name, int[] values, int[] counts, int requested, int? exact, int[] exactValues, int[] exactCounts, int? lower, int[] lowerValues, int[] lowerCounts, int? upper, int[] upperValues, int[] upperCounts) => [name, values, counts, requested, exact, exactValues, exactCounts, lower, lowerValues, lowerCounts, upper, upperValues, upperCounts];

    private static Dictionary<int, int> Stock(int[] values, int[] counts) => values.Zip(counts).ToDictionary(x => x.First, x => x.Second);
    private static void AssertBreakdown(NoteBreakdown? actual, int? amount, Dictionary<int, int> expected)
    {
        if (amount is null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        Assert.Equal(amount, actual.Amount);
        Assert.Equal(expected.OrderBy(x => x.Key), actual.Notes.OrderBy(x => x.Key));
    }
}
