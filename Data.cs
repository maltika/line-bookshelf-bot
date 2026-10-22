using Microsoft.EntityFrameworkCore;

// ผู้ใช้ LINE 1 คน = 1 แถว (ผูกด้วย LineUserId)
public class AppUser
{
    public int Id { get; set; }
    public string LineUserId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Book> Books { get; set; } = new();
}

// หนังสือ 1 เล่ม เป็นของผู้ใช้ 1 คน
public class Book
{
    public int Id { get; set; }
    public int AppUserId { get; set; }
    public AppUser? AppUser { get; set; }
    public string Title { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Book> Books => Set<Book>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().Property(u => u.LineUserId).HasMaxLength(64);
        b.Entity<AppUser>().HasIndex(u => u.LineUserId).IsUnique(); // กัน user ซ้ำ
        b.Entity<Book>().Property(x => x.Title).HasMaxLength(200);
    }
}