using EasyTolet.AdminService.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyTolet.AdminService;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<AdminOutboxMessage> AdminOutboxMessages => Set<AdminOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var role = modelBuilder.Entity<Role>();
        role.ToTable("Roles");
        role.HasKey(r => r.RoleId);
        role.Property(r => r.RoleId).ValueGeneratedOnAdd();
        role.Property(r => r.RoleCode).HasMaxLength(10).IsRequired();
        role.Property(r => r.RoleName).HasMaxLength(100).IsRequired();
        role.Property(r => r.Description).HasMaxLength(500);
        role.Property(r => r.IsActive).HasDefaultValue(true);
        role.Property(r => r.CreatedDate).HasPrecision(6).HasDefaultValueSql("CURRENT_TIMESTAMP(6)")
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        role.Property(r => r.UpdatedDate).HasPrecision(6)
            .HasConversion(value => value, value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : (DateTime?)null);
        role.HasIndex(r => r.RoleCode).IsUnique().HasDatabaseName("UK_Roles_RoleCode");
        role.HasIndex(r => r.ParentRoleId).HasDatabaseName("FK_Roles_ParentRole");
        role.HasOne(r => r.ParentRole).WithMany(r => r.Children).HasForeignKey(r => r.ParentRoleId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_Roles_ParentRole");

        var outbox = modelBuilder.Entity<AdminOutboxMessage>();
        outbox.ToTable("AdminOutboxMessages");
        outbox.HasKey(m => m.Id);
        outbox.Property(m => m.Id).ValueGeneratedOnAdd();
        outbox.Property(m => m.EventId).HasConversion<string>().HasMaxLength(36);
        outbox.HasIndex(m => m.EventId).IsUnique();
        outbox.Property(m => m.Payload).HasColumnType("longtext").IsRequired();
        outbox.Property(m => m.OccurredAtUtc).HasPrecision(6);
        outbox.Property(m => m.PublishedAtUtc).HasPrecision(6);
        outbox.HasIndex(m => new { m.PublishedAtUtc, m.Id });
    }
}
