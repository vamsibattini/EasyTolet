using Microsoft.EntityFrameworkCore;

namespace EasyTolet.WishlistService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
