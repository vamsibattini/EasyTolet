using Microsoft.EntityFrameworkCore;

namespace EasyTolet.AgreementService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
