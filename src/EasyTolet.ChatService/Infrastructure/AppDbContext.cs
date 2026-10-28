using Microsoft.EntityFrameworkCore;

namespace EasyTolet.ChatService;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }
}
