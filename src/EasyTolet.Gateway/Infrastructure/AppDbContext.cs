using Microsoft.EntityFrameworkCore;

namespace EasyTolet.Gateway;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
