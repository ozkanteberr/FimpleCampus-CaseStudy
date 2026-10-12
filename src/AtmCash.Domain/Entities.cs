namespace AtmCash.Domain;

public sealed class Atm
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public AtmState State { get; set; }
    public List<Cassette> Cassettes { get; set; } = [];
}

public sealed class Cassette
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AtmId { get; set; }
    public int Number { get; set; }
    public CassetteKind Kind { get; set; }
    public Currency? Currency { get; set; }
    public int? Denomination { get; set; }
    public bool LowCash { get; set; }
    public List<CassetteStock> Stocks { get; set; } = [];
}

public sealed class CassetteStock
{
    public Guid CassetteId { get; set; }
    public Currency Currency { get; set; }
    public int Denomination { get; set; }
    public int Count { get; set; }
    public int Reserved { get; set; }
}

public sealed class Movement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CassetteId { get; set; }
    public Currency Currency { get; set; }
    public int Denomination { get; set; }
    public MovementType Type { get; set; }
    public int CountDelta { get; set; }
    public int ReservedDelta { get; set; }
    public Guid OperationId { get; set; }
    public string User { get; set; } = "system";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ExchangeRate
{
    public Currency Currency { get; set; }
    public decimal TryRate { get; set; }
}

public sealed class OutboxEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AtmId { get; set; }
    public Guid CassetteId { get; set; }
    public string Type { get; set; } = "LOW_CASH";
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
