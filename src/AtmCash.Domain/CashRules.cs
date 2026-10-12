namespace AtmCash.Domain;

public enum Currency { TRY, USD, EUR }
public enum AtmState { UNCONFIGURED, CONFIGURED, LOADED, IN_SERVICE, MAINTENANCE }
public enum CassetteKind { DepositOnly, Recycling }
public enum MovementType
{
    LOAD, UNLOAD, WITHDRAWAL, DEPOSIT, RESERVATION,
    RESERVATION_RELEASE, RECONCILIATION_ADJUSTMENT
}

public sealed class BusinessException(string code, string message, int status = 422) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public static class CashRules
{
    public const int Capacity = 100;
    public static readonly int[] Denominations = [20, 50, 100, 500];

    public static void ValidateNote(Currency currency, int denomination)
    {
        if (!Enum.IsDefined(currency) || !Denominations.Contains(denomination))
            throw new BusinessException("INVALID_NOTE", "Only TRY, USD, EUR and denominations 20, 50, 100, 500 are supported.");
    }

    public static void Require(bool condition, string code, string message, int status = 422)
    {
        if (!condition) throw new BusinessException(code, message, status);
    }

    public static void RequireMaintenance(AtmState state)
    {
        Require(state is AtmState.UNCONFIGURED or AtmState.CONFIGURED or AtmState.MAINTENANCE,
            "INVALID_ATM_STATE", "Operation requires initial setup or MAINTENANCE.", 409);
    }

    public static void ValidateConfiguration(IReadOnlyList<CassetteDefinition> definitions)
    {
        Require(!definitions.Any(x => x is null), "INVALID_CONFIGURATION", "Cassette definitions cannot contain null entries.");
        Require(definitions.Count == 5 && definitions.Select(x => x.Number).Order().SequenceEqual(new[] { 1, 2, 3, 4, 5 }),
            "INVALID_CONFIGURATION", "Exactly five cassettes numbered 1 through 5 are required.");
        Require(definitions.Count(x => x.Kind == CassetteKind.DepositOnly) == 1
            && definitions.Count(x => x.Kind == CassetteKind.Recycling) == 4,
            "INVALID_CONFIGURATION", "One deposit-only and four recycling cassettes are required.");
        foreach (var definition in definitions)
        {
            if (definition.Kind == CassetteKind.Recycling)
            {
                Require(definition.Currency.HasValue && definition.Denomination.HasValue,
                    "INVALID_CONFIGURATION", "A recycling cassette needs a currency and denomination.");
                ValidateNote(definition.Currency!.Value, definition.Denomination!.Value);
            }
            else Require(definition.Currency is null && definition.Denomination is null,
                "INVALID_CONFIGURATION", "Deposit-only cassettes cannot be assigned a currency or denomination.");
        }
        var foreign = definitions.Count(x => x.Kind == CassetteKind.Recycling && x.Currency != Currency.TRY);
        Require(foreign is >= 1 and <= 2, "INVALID_CONFIGURATION", "One or two recycling cassettes must hold foreign currency.");
    }

    public static void ValidateTransition(AtmState from, AtmState to)
    {
        var allowed = (from, to) is
            (AtmState.LOADED, AtmState.IN_SERVICE) or
            (AtmState.IN_SERVICE, AtmState.MAINTENANCE) or
            (AtmState.MAINTENANCE, AtmState.IN_SERVICE);
        Require(allowed, "INVALID_STATE_TRANSITION", $"Cannot transition from {from} to {to}.", 409);
    }
}

public sealed record CassetteDefinition(int Number, CassetteKind Kind, Currency? Currency, int? Denomination);
