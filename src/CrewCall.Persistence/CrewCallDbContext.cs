using Microsoft.EntityFrameworkCore;

namespace CrewCall.Persistence;

/// <summary>
/// The CrewCall database context. Intentionally has no entities yet: modules add their own
/// entity configurations in their own schema (see <see cref="DatabaseSchemas"/>).
/// </summary>
public sealed class CrewCallDbContext(DbContextOptions<CrewCallDbContext> options) : DbContext(options)
{
}
