using Microsoft.EntityFrameworkCore;

namespace EasyTolet.UserService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
