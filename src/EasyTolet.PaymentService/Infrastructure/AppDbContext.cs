using Microsoft.EntityFrameworkCore;

namespace EasyTolet.PaymentService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
