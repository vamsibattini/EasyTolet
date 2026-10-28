using Microsoft.EntityFrameworkCore;

namespace EasyTolet.SearchService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
