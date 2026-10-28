using Microsoft.EntityFrameworkCore;

namespace EasyTolet.ReviewService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
