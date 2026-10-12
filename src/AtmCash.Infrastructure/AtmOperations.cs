using AtmCash.Domain;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure;

public sealed record CashEntry(int CassetteNumber, Currency Currency, int Denomination, int Count);
public sealed record AtmSummary(Guid Id, string Name, AtmState State);
public sealed record StockReport(Currency Currency, int Denomination, int Count, int Reserved, int Available);
public sealed record CassetteReport(int Number, CassetteKind Kind, Currency? Currency, int? Denomination,
    int Capacity, int Count, int Reserved, int Available, IReadOnlyList<StockReport> Notes);
public sealed record CurrencyTotal(Currency Currency, decimal Balance, decimal Reserved, decimal Available);
public sealed record AtmReport(Guid Id, string Name, AtmState State, IReadOnlyList<CassetteReport> Cassettes,
    IReadOnlyList<CurrencyTotal> Totals, decimal OverallTryBalance);
public sealed record BalanceDifference(int CassetteNumber, Currency Currency, int Denomination,
    int StoredCount, int LedgerCount, int StoredReserved, int LedgerReserved);
public sealed record ConsistencyResult(Guid AtmId, bool Consistent, IReadOnlyList<BalanceDifference> Differences);

public sealed class AtmOperations(CashDbContext db, TimeProvider clock)
{
    public async Task<Atm> Find(Guid id, CancellationToken ct) =>
        await db.Atms.Include(x => x.Cassettes).ThenInclude(x => x.Stocks).SingleOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new BusinessException("ATM_NOT_FOUND", "ATM was not found.", 404);

    private async Task Lock(Guid id, CancellationToken ct)
    {
        // Transaction-scoped cross-process lock for all writes affecting the same ATM.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"atm:" + id}, 0))", ct);
    }

    public async Task<AtmSummary> Create(string name, CancellationToken ct)
    {
        CashRules.Require(!string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 100,
            "INVALID_NAME", "ATM name must contain 1 to 100 characters.");
        var atm = new Atm { Name = name.Trim() };
        db.Atms.Add(atm);
        await db.SaveChangesAsync(ct);
        return Summary(atm);
    }

    public async Task<AtmSummary> Configure(Guid id, IReadOnlyList<CassetteDefinition> definitions, CancellationToken ct)
    {
        CashRules.ValidateConfiguration(definitions);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock(id, ct);
        var atm = await Find(id, ct);
        CashRules.RequireMaintenance(atm.State);
        CashRules.Require(atm.Cassettes.All(x => x.Stocks.All(s => s.Count == 0 && s.Reserved == 0)),
            "CASSETTE_NOT_EMPTY", "All cassettes must be empty before reconfiguration.", 409);
        foreach (var existing in atm.Cassettes)
        {
            db.Stocks.RemoveRange(existing.Stocks);
            existing.Stocks.Clear();
        }
        foreach (var definition in definitions)
        {
            var cassette = atm.Cassettes.SingleOrDefault(x => x.Number == definition.Number);
            if (cassette is null)
            {
                cassette = new Cassette { AtmId = atm.Id, Number = definition.Number };
                atm.Cassettes.Add(cassette);
                db.Cassettes.Add(cassette);
            }
            cassette.Kind = definition.Kind;
            cassette.Currency = definition.Currency;
            cassette.Denomination = definition.Denomination;
        }
        if (atm.State == AtmState.UNCONFIGURED) atm.State = AtmState.CONFIGURED;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Summary(atm);
    }

    public async Task<AtmSummary> MoveCash(Guid id, IReadOnlyList<CashEntry> entries, bool load, string user, CancellationToken ct)
    {
        CashRules.Require(entries.Count > 0, "EMPTY_CASH_LIST", "At least one cash entry is required.");
        CashRules.Require(entries.Select(x => (x.CassetteNumber, x.Currency, x.Denomination)).Distinct().Count() == entries.Count,
            "DUPLICATE_CASH_ENTRY", "Duplicate cassette/currency/denomination entries are not allowed.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock(id, ct);
        var atm = await Find(id, ct);
        CashRules.Require(atm.State is AtmState.CONFIGURED or AtmState.MAINTENANCE,
            "INVALID_ATM_STATE", "Cash loading/unloading requires CONFIGURED or MAINTENANCE.", 409);
        var operationId = Guid.NewGuid();
        foreach (var entry in entries)
        {
            CashRules.ValidateNote(entry.Currency, entry.Denomination);
            CashRules.Require(entry.Count is > 0 and <= 100, "INVALID_COUNT", "Count must be from 1 to 100.");
            var cassette = atm.Cassettes.SingleOrDefault(x => x.Number == entry.CassetteNumber)
                ?? throw new BusinessException("CASSETTE_NOT_FOUND", "Cassette number does not exist.", 404);
            CashRules.Require(cassette.Kind == CassetteKind.DepositOnly
                || (cassette.Currency == entry.Currency && cassette.Denomination == entry.Denomination),
                "CASSETTE_NOTE_MISMATCH", "Note does not match the recycling cassette configuration.");
            var stock = cassette.Stocks.SingleOrDefault(x => x.Currency == entry.Currency && x.Denomination == entry.Denomination);
            if (stock is null)
            {
                stock = new CassetteStock { CassetteId = cassette.Id, Currency = entry.Currency, Denomination = entry.Denomination };
                cassette.Stocks.Add(stock);
            }
            var delta = load ? entry.Count : -entry.Count;
            CashRules.Require(stock.Count + delta >= stock.Reserved, "INSUFFICIENT_CASH", "Not enough unreserved notes to unload.", 409);
            CashRules.Require(cassette.Stocks.Sum(x => x.Count) + delta <= CashRules.Capacity,
                "CASSETTE_FULL", "Cassette capacity is 100 banknotes.", 409);
            stock.Count += delta;
            db.Movements.Add(new Movement
            {
                CassetteId = cassette.Id, Currency = entry.Currency, Denomination = entry.Denomination,
                Type = load ? MovementType.LOAD : MovementType.UNLOAD, CountDelta = delta,
                OperationId = operationId, User = user, CreatedAt = clock.GetUtcNow()
            });
        }
        if (atm.State == AtmState.CONFIGURED && load
            && atm.Cassettes.Any(x => x.Kind == CassetteKind.Recycling && x.Stocks.Any(s => s.Count > 0)))
            atm.State = AtmState.LOADED;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Summary(atm);
    }

    public async Task<AtmSummary> ChangeState(Guid id, AtmState target, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock(id, ct);
        var atm = await Find(id, ct);
        CashRules.ValidateTransition(atm.State, target);
        if (target == AtmState.IN_SERVICE)
        {
            CashRules.ValidateConfiguration(atm.Cassettes.Select(x => new CassetteDefinition(x.Number, x.Kind, x.Currency, x.Denomination)).ToArray());
            CashRules.Require(atm.Cassettes.Where(x => x.Kind == CassetteKind.Recycling).Any(x => x.Stocks.Sum(s => s.Count) > 0),
                "ATM_HAS_NO_DISPENSABLE_CASH", "Load at least one recycling cassette before putting the ATM in service.", 409);
        }
        atm.State = target;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Summary(atm);
    }

    public async Task<AtmReport> Report(Guid id, CancellationToken ct)
    {
        var atm = await Find(id, ct);
        var rates = await db.ExchangeRates.AsNoTracking().ToDictionaryAsync(x => x.Currency, x => x.TryRate, ct);
        var cassettes = atm.Cassettes.OrderBy(x => x.Number).Select(x => new CassetteReport(
            x.Number, x.Kind, x.Currency, x.Denomination, CashRules.Capacity, x.Stocks.Sum(s => s.Count),
            x.Stocks.Sum(s => s.Reserved), x.Stocks.Sum(s => s.Count - s.Reserved),
            x.Stocks.OrderBy(s => s.Currency).ThenBy(s => s.Denomination).Select(s =>
                new StockReport(s.Currency, s.Denomination, s.Count, s.Reserved, s.Count - s.Reserved)).ToArray())).ToArray();
        var stocks = atm.Cassettes.SelectMany(x => x.Stocks).ToArray();
        var totals = Enum.GetValues<Currency>().Select(currency => new CurrencyTotal(currency,
            stocks.Where(x => x.Currency == currency).Sum(x => (decimal)x.Count * x.Denomination),
            stocks.Where(x => x.Currency == currency).Sum(x => (decimal)x.Reserved * x.Denomination),
            stocks.Where(x => x.Currency == currency).Sum(x => (decimal)(x.Count - x.Reserved) * x.Denomination))).ToArray();
        return new AtmReport(atm.Id, atm.Name, atm.State, cassettes, totals, totals.Sum(x => x.Balance * rates[x.Currency]));
    }

    public async Task<ConsistencyResult> CheckConsistency(Guid id, CancellationToken ct)
    {
        // Use a single snapshot to avoid false differences during concurrent committed writes.
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var atm = await Find(id, ct);
        var cassetteIds = atm.Cassettes.Select(x => x.Id).ToArray();
        var movements = await db.Movements.AsNoTracking().Where(x => cassetteIds.Contains(x.CassetteId)).ToListAsync(ct);
        var differences = new List<BalanceDifference>();
        foreach (var cassette in atm.Cassettes)
        {
            var keys = cassette.Stocks.Select(x => (x.Currency, x.Denomination))
                .Concat(movements.Where(x => x.CassetteId == cassette.Id).Select(x => (x.Currency, x.Denomination))).Distinct();
            foreach (var (currency, denomination) in keys)
            {
                var stock = cassette.Stocks.SingleOrDefault(x => x.Currency == currency && x.Denomination == denomination);
                var records = movements.Where(x => x.CassetteId == cassette.Id && x.Currency == currency && x.Denomination == denomination).ToArray();
                var count = records.Sum(x => x.CountDelta);
                var reserved = records.Sum(x => x.ReservedDelta);
                if ((stock?.Count ?? 0) != count || (stock?.Reserved ?? 0) != reserved)
                    differences.Add(new BalanceDifference(cassette.Number, currency, denomination, stock?.Count ?? 0, count, stock?.Reserved ?? 0, reserved));
            }
        }
        await transaction.CommitAsync(ct);
        return new ConsistencyResult(id, differences.Count == 0, differences);
    }

    public static AtmSummary Summary(Atm atm) => new(atm.Id, atm.Name, atm.State);
}
