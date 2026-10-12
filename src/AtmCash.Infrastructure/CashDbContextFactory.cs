using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AtmCash.Infrastructure;

public sealed class CashDbContextFactory : IDesignTimeDbContextFactory<CashDbContext>
{
    public CashDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__CashDb")
            ?? "Host=localhost;Port=55432;Database=atm_cash;Username=atm_app;Password=atm_local_password";
        return new CashDbContext(new DbContextOptionsBuilder<CashDbContext>().UseNpgsql(connection).Options);
    }
}
