using CrewCall.Resources;
using CrewCall.Resources.Equipment;
using CrewCall.Resources.Vehicles;
using CrewCall.WorkOrders;
using CrewCall.WorkOrders.Customers;
using CrewCall.WorkOrders.Sites;
using CrewCall.Workforce;
using CrewCall.Workforce.Skills;
using CrewCall.Workforce.Teams;
using CrewCall.Workforce.Technicians;
using Microsoft.EntityFrameworkCore;

namespace CrewCall.Persistence;

/// <summary>
/// The CrewCall database context: one physical database, one schema per module (<see cref="DatabaseSchemas"/>).
/// Each module works through its own narrow interface and sees only the sets it owns.
/// Entity mapping lives in <c>Configurations/</c>, one <see cref="IEntityTypeConfiguration{TEntity}"/> per entity.
/// </summary>
public sealed class CrewCallDbContext(DbContextOptions<CrewCallDbContext> options)
    : DbContext(options), IWorkOrdersDbContext, IWorkforceDbContext, IResourcesDbContext
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Site> Sites => Set<Site>();

    public DbSet<Technician> Technicians => Set<Technician>();

    public DbSet<Skill> Skills => Set<Skill>();

    public DbSet<TechnicianSkill> TechnicianSkills => Set<TechnicianSkill>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    public DbSet<EquipmentItem> Equipment => Set<EquipmentItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CrewCallDbContext).Assembly);
}
