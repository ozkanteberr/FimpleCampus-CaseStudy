using System.Text.Json.Serialization;
using AtmCash.Domain;
using AtmCash.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NoteSelector>();
builder.Services.AddSingleton(sp => new CardIdentity(sp.GetRequiredService<IConfiguration>()["CardIdentity:HmacKey"]
    ?? throw new InvalidOperationException("CardIdentity:HmacKey must be configured.")));
builder.Services.AddDbContext<CashDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("CashDb")));
builder.Services.AddScoped<AtmOperations>();
builder.Services.AddScoped<QuoteOperations>();
builder.Services.AddScoped<QuoteExpiryOperations>();
builder.Services.AddScoped<DepositOperations>();
builder.Services.AddHostedService<QuoteExpiryWorker>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgresql");

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages(async context =>
{
    var writer = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
    await writer.TryWriteAsync(new ProblemDetailsContext
    {
        HttpContext = context.HttpContext,
        ProblemDetails = new ProblemDetails
        {
            Status = context.HttpContext.Response.StatusCode,
            Title = "Request could not be processed.",
            Extensions = { ["code"] = "HTTP_" + context.HttpContext.Response.StatusCode }
        }
    });
});
app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ATM Cash Management v1"));
app.MapHealthChecks("/health");

var api = app.MapGroup("/api");
api.MapPost("/atms", async (CreateAtmRequest request, AtmOperations service, CancellationToken ct) =>
{
    var result = await service.Create(request.Name, ct);
    return Results.Created($"/api/atms/{result.Id}", result);
}).WithName("CreateAtm");
api.MapGet("/atms", async (CashDbContext db, CancellationToken ct) =>
    await db.Atms.AsNoTracking().OrderBy(x => x.Name).Select(x => new AtmSummary(x.Id, x.Name, x.State)).ToListAsync(ct));
api.MapGet("/atms/{id:guid}", async (Guid id, AtmOperations service, CancellationToken ct) => AtmOperations.Summary(await service.Find(id, ct)));
api.MapPut("/atms/{id:guid}/configuration", async (Guid id, ConfigurationRequest request, AtmOperations service, CancellationToken ct) =>
    await service.Configure(id, request.Cassettes ?? throw new BusinessException("INVALID_REQUEST", "Cassettes list is required.", 400), ct));
api.MapPost("/atms/{id:guid}/load", async (Guid id, CashRequest request, HttpContext http, AtmOperations service, CancellationToken ct) =>
    await service.MoveCash(id, request.Notes ?? throw new BusinessException("INVALID_REQUEST", "Notes list is required.", 400), true, RequestUser(http), ct));
api.MapPost("/atms/{id:guid}/unload", async (Guid id, CashRequest request, HttpContext http, AtmOperations service, CancellationToken ct) =>
    await service.MoveCash(id, request.Notes ?? throw new BusinessException("INVALID_REQUEST", "Notes list is required.", 400), false, RequestUser(http), ct));
api.MapPost("/atms/{id:guid}/state", async (Guid id, StateRequest request, AtmOperations service, CancellationToken ct) =>
    await service.ChangeState(id, request.State, ct));
api.MapPost("/atms/{id:guid}/quotes", async (Guid id, QuoteRequest request, QuoteOperations service, CancellationToken ct) =>
    await service.Create(id, request.CardNumber, request.Currency, request.Amount, ct)).WithName("CreateQuote");
api.MapPost("/quotes/{id:guid}/confirm", async (Guid id, ConfirmQuoteRequest request, HttpContext http, QuoteOperations service, CancellationToken ct) =>
    await service.Confirm(id, request.AlternativeId, http.Request.Headers["Idempotency-Key"].ToString(), ct)).WithName("ConfirmQuote");
api.MapPost("/quotes/{id:guid}/cancel", async (Guid id, QuoteOperations service, CancellationToken ct) =>
    await service.Cancel(id, ct)).WithName("CancelQuote");
api.MapPost("/atms/{id:guid}/deposits", async (Guid id, DepositRequest request, DepositOperations service, CancellationToken ct) =>
    await service.Deposit(id, request.Notes ?? throw new BusinessException("INVALID_REQUEST", "Notes list is required.", 400),
        request.UnrecognisedCount, ct)).WithName("DepositCash");
api.MapGet("/atms/{id:guid}/report", async (Guid id, AtmOperations service, CancellationToken ct) => await service.Report(id, ct));
api.MapGet("/atms/{id:guid}/consistency", async (Guid id, AtmOperations service, CancellationToken ct) => await service.CheckConsistency(id, ct));
api.MapGet("/exchange-rates", async (CashDbContext db, CancellationToken ct) => await db.ExchangeRates.AsNoTracking().ToListAsync(ct));

// Apply migrations before the server accepts requests.
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<CashDbContext>().Database.MigrateAsync();

await app.RunAsync();

static string RequestUser(HttpContext http)
{
    var user = http.Request.Headers["X-User"].ToString();
    CashRules.Require(!string.IsNullOrWhiteSpace(user) && user.Length <= 100, "INVALID_USER", "X-User header (1 to 100 characters) is required.", 400);
    return user;
}

public sealed record CreateAtmRequest(string Name);
public sealed record ConfigurationRequest(List<CassetteDefinition> Cassettes);
public sealed record CashRequest(List<CashEntry> Notes);
public sealed record StateRequest(AtmState State);
public sealed record QuoteRequest(string CardNumber, Currency Currency, int Amount);
public sealed record ConfirmQuoteRequest(Guid AlternativeId);
public sealed record DepositRequest(List<DepositEntry> Notes, int UnrecognisedCount);

public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var business = exception as BusinessException;
        var status = business?.Status ?? (exception is BadHttpRequestException ? 400 : 500);
        context.Response.StatusCode = status;
        if (status == 500) logger.LogError(exception, "Unhandled request failure {TraceId}", context.TraceIdentifier);
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = business?.Code ?? (status == 400 ? "INVALID_REQUEST" : "INTERNAL_ERROR"),
                Detail = business?.Message ?? (status == 400 ? "Request body is invalid." : "An unexpected error occurred."),
                Extensions = { ["code"] = business?.Code ?? (status == 400 ? "INVALID_REQUEST" : "INTERNAL_ERROR"), ["traceId"] = context.TraceIdentifier }
            }
        });
        return true;
    }
}

public partial class Program;
