using Daylo.Core;
using Microsoft.EntityFrameworkCore;

namespace Daylo.Web.Data;

/// <summary>The one Daylo database (PostgreSQL, database "daylo").</summary>
public sealed class DayloDbContext(DbContextOptions<DayloDbContext> options) : DbContext(options)
{
    public DbSet<Worker> Workers => Set<Worker>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<WorkTask> Tasks => Set<WorkTask>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<ActivityLog> ActivityLog => Set<ActivityLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // Enums are stored by name: the database stays readable.
        configuration.Properties<Priority>().HaveConversion<string>();
        configuration.Properties<WorkTaskStatus>().HaveConversion<string>();
        configuration.Properties<IncidentStatus>().HaveConversion<string>();
        configuration.Properties<AssignmentStatus>().HaveConversion<string>();
        configuration.Properties<ResourceType>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Worker>()
            .HasMany(worker => worker.Skills)
            .WithMany()
            .UsingEntity(
                "WorkerSkills",
                skill => skill.HasOne(typeof(Skill)).WithMany().HasForeignKey("SkillId"),
                worker => worker.HasOne(typeof(Worker)).WithMany().HasForeignKey("WorkerId"));

        model.Entity<Skill>().HasIndex(skill => skill.Code).IsUnique();

        model.Entity<WorkTask>(task =>
        {
            task.ToTable("Tasks");
            task.HasIndex(t => t.PlannedStart);
            task.Property(t => t.Version).IsRowVersion();
        });

        model.Entity<Incident>().Property(incident => incident.Version).IsRowVersion();

        model.Entity<Assignment>().HasOne(assignment => assignment.Task).WithMany(task => task.Assignments).HasForeignKey(a => a.TaskId);

        model.Entity<ActivityLog>(log =>
        {
            log.ToTable("ActivityLog");
            log.HasIndex(entry => entry.OccurredAt);
        });
    }
}
