using Microsoft.EntityFrameworkCore;

namespace EasyTolet.NotificationService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
