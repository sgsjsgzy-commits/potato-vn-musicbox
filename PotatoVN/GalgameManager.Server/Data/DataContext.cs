using GalgameManager.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace GalgameManager.Server.Data;

public class DataContext(DbContextOptions<DataContext> options) : DbContext(options)
{
    public DbSet<User> User { get; init; } = null!;
    public DbSet<Galgame> Galgame { get; init; } = null!;
    public DbSet<GalgameDeleted> GalgameDeleted { get; init; } = null!;
    public DbSet<PlayLog> GalPlayLog { get; init; } = null!;
    public DbSet<Category> Category { get; init; } = null!;
    public DbSet<OssRecord> OssRecords { get; set; } = null!;
    public DbSet<Character> Character { get; init; } = null!;
    public DbSet<Staff> Staff { get; init; } = null!;
    public DbSet<StaffGame> StaffGame { get; init; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StaffGame>().HasKey(sg => new{sg.StaffId, sg.GameId});
        modelBuilder.Entity<StaffGame>()
            .HasOne(sg => sg.Staff).WithMany(s => s.StaffGames)
            .HasForeignKey(sg => sg.StaffId);
        modelBuilder.Entity<StaffGame>()
            .HasOne(sg => sg.Game).WithMany(g => g.StaffGames)
            .HasForeignKey(sg => sg.GameId);
        
        base.OnModelCreating(modelBuilder);
    }
}