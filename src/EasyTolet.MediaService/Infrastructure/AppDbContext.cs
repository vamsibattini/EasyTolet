using Microsoft.EntityFrameworkCore;

namespace EasyTolet.MediaService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
