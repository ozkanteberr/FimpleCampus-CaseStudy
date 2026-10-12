using AtmCash.Domain;

namespace AtmCash.Tests;

public sealed class CardDailyLimitTests
{
    private const string Secret = "local-test-card-identity-key-with-32-plus-bytes";

    [Fact]
    public void Card_fingerprint_is_stable_normalized_and_keyed()
    {
        var identity = new CardIdentity(Secret);
        var fingerprint = identity.Fingerprint("4111 1111-1111 1111");

        Assert.Equal(64, fingerprint.Length);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
        Assert.Equal(fingerprint, identity.Fingerprint("4111111111111111"));
        Assert.NotEqual(fingerprint, identity.Fingerprint("4111111111111112"));
        Assert.NotEqual(fingerprint, new CardIdentity(Secret + "-different").Fingerprint("4111111111111111"));
        Assert.DoesNotContain("4111111111111111", fingerprint);
    }

    [Fact]
    public void Card_identity_rejects_weak_key_and_invalid_numbers()
    {
        AssertError("INVALID_CARD_KEY", () => new CardIdentity("short"));
        var identity = new CardIdentity(Secret);
        AssertError("INVALID_CARD_NUMBER", () => identity.Fingerprint("4111x11111111111"));
        AssertError("INVALID_CARD_NUMBER", () => identity.Fingerprint("123"));
    }

    [Fact]
    public void Daily_limit_counts_reserved_and_used_amounts_per_currency()
    {
        Assert.Equal(20_000, CardDailyLimitRules.Limit(Currency.TRY));
        Assert.Equal(1_000, CardDailyLimitRules.Limit(Currency.USD));
        Assert.Equal(1_000, CardDailyLimitRules.Limit(Currency.EUR));

        var usage = new CardDailyUsage { Currency = Currency.USD };
        CardDailyLimitRules.Reserve(usage, 900);
        Assert.Equal(100, CardDailyLimitRules.Remaining(usage));
        AssertError("DAILY_LIMIT_EXCEEDED", () => CardDailyLimitRules.Reserve(usage, 101));
        Assert.Equal(900, usage.ReservedAmount);

        CardDailyLimitRules.Confirm(usage, 900, 700);
        Assert.Equal(700, usage.UsedAmount);
        Assert.Equal(0, usage.ReservedAmount);
        Assert.Equal(300, CardDailyLimitRules.Remaining(usage));
        CardDailyLimitRules.Reserve(usage, 300);
        Assert.Equal(0, CardDailyLimitRules.Remaining(usage));
        CardDailyLimitRules.Release(usage, 300);
        Assert.Equal(300, CardDailyLimitRules.Remaining(usage));
    }

    [Fact]
    public void Utc_calendar_day_ignores_local_offset()
    {
        var beforeMidnightUtc = new DateTimeOffset(2026, 10, 13, 2, 59, 59, TimeSpan.FromHours(3));
        var afterMidnightUtc = beforeMidnightUtc.AddSeconds(1);

        Assert.Equal(new DateOnly(2026, 10, 12), CardDailyLimitRules.UtcDate(beforeMidnightUtc));
        Assert.Equal(new DateOnly(2026, 10, 13), CardDailyLimitRules.UtcDate(afterMidnightUtc));
    }

    [Fact]
    public void Limit_transitions_reject_invalid_release_and_confirmation()
    {
        var usage = new CardDailyUsage { Currency = Currency.TRY };
        CardDailyLimitRules.Reserve(usage, 500);
        AssertError("INVALID_LIMIT_RELEASE", () => CardDailyLimitRules.Release(usage, 501));
        AssertError("INVALID_LIMIT_CONFIRMATION", () => CardDailyLimitRules.Confirm(usage, 500, 501));
        Assert.Equal(500, usage.ReservedAmount);
        Assert.Equal(0, usage.UsedAmount);
    }

    private static void AssertError(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<BusinessException>(action).Code);
}
