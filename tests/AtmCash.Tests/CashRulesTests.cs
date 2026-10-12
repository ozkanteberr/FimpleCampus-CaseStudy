using AtmCash.Domain;

namespace AtmCash.Tests;

public sealed class CashRulesTests
{
    public static CassetteDefinition[] ValidConfiguration =>
    [
        new(1, CassetteKind.Recycling, Currency.TRY, 100),
        new(2, CassetteKind.Recycling, Currency.TRY, 50),
        new(3, CassetteKind.Recycling, Currency.TRY, 20),
        new(4, CassetteKind.Recycling, Currency.USD, 100),
        new(5, CassetteKind.DepositOnly, null, null)
    ];

    [Fact]
    public void One_foreign_currency_configuration_is_valid() => CashRules.ValidateConfiguration(ValidConfiguration);

    [Fact]
    public void Duplicate_currency_denomination_is_allowed()
    {
        var cassettes = ValidConfiguration;
        cassettes[2] = new(3, CassetteKind.Recycling, Currency.TRY, 100);
        CashRules.ValidateConfiguration(cassettes);
    }

    [Fact]
    public void Two_foreign_currency_cassettes_are_allowed()
    {
        var cassettes = ValidConfiguration;
        cassettes[2] = new(3, CassetteKind.Recycling, Currency.EUR, 50);
        CashRules.ValidateConfiguration(cassettes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void Invalid_foreign_currency_count_is_rejected(int foreignCount)
    {
        var cassettes = ValidConfiguration;
        for (var i = 0; i < 4; i++) cassettes[i] = new(i + 1, CassetteKind.Recycling, i < foreignCount ? Currency.USD : Currency.TRY, 100);
        Assert.Throws<BusinessException>(() => CashRules.ValidateConfiguration(cassettes));
    }

    [Fact]
    public void Deposit_only_cassette_cannot_have_a_fixed_currency()
    {
        var cassettes = ValidConfiguration;
        cassettes[4] = new(5, CassetteKind.DepositOnly, Currency.TRY, 100);
        Assert.Throws<BusinessException>(() => CashRules.ValidateConfiguration(cassettes));
    }

    [Theory]
    [InlineData(AtmState.UNCONFIGURED, AtmState.IN_SERVICE)]
    [InlineData(AtmState.CONFIGURED, AtmState.IN_SERVICE)]
    [InlineData(AtmState.IN_SERVICE, AtmState.CONFIGURED)]
    [InlineData(AtmState.IN_SERVICE, AtmState.IN_SERVICE)]
    public void Invalid_state_transition_is_rejected(AtmState from, AtmState to) =>
        Assert.Equal("INVALID_STATE_TRANSITION", Assert.Throws<BusinessException>(() => CashRules.ValidateTransition(from, to)).Code);
}
