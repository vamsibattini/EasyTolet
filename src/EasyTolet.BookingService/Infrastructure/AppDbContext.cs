using Microsoft.EntityFrameworkCore;

namespace EasyTolet.BookingService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
