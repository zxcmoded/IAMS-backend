using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Iams.Persistence;

public class DesignTimeFactory : IDesignTimeDbContextFactory<IamsDbContext>
{
    public IamsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IamsDbContext>()
            .UseSqlServer("Server=.;Database=Iams;Trusted_Connection=True;TrustServerCertificate=True",
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory"))
            .Options;
        return new IamsDbContext(options);
    }
}
