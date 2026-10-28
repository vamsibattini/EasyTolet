using Microsoft.EntityFrameworkCore;

namespace EasyTolet.PropertyService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
