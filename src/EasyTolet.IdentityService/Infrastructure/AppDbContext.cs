using Microsoft.EntityFrameworkCore;

namespace EasyTolet.IdentityService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
