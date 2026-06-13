using Microsoft.EntityFrameworkCore;
using RoomBooking.Infrastructure;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace RoomBooking.Migrations;

public class AppDbContext : DbContext
{
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        modelBuilder
            .Entity<Reservation>()
            .Property(r => r.Status)
            .HasConversion<string>();

        base.OnModelCreating(modelBuilder);
    }
}
