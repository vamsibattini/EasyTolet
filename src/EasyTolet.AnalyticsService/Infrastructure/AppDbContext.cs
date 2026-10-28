using Microsoft.EntityFrameworkCore;

namespace EasyTolet.AnalyticsService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
