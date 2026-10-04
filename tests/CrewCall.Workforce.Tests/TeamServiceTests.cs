using CrewCall.Workforce.Teams;
using CrewCall.Workforce.Technicians;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Workforce.Tests;

public sealed class TeamServiceTests(WorkforceDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_saves_a_valid_team_as_active()
    {
        await using var scope = database.CreateScope();
        var teams = scope.ServiceProvider.GetRequiredService<TeamService>();

        var outcome = await teams.CreateAsync(new CreateTeam("  Fiber North  ", null), Cancellation);

        var created = Assert.IsType<CreateTeamOutcome.Created>(outcome);
        Assert.Equal("Fiber North", created.Team.Name);
        Assert.True(created.Team.IsActive);
    }

    [Fact]
    public async Task Create_rejects_an_empty_name()
    {
        await using var scope = database.CreateScope();
        var teams = scope.ServiceProvider.GetRequiredService<TeamService>();

        var outcome = await teams.CreateAsync(new CreateTeam(null, null), Cancellation);

        Assert.Contains("name", Assert.IsType<CreateTeamOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Fact]
    public async Task AddMember_assigns_a_technician_to_a_team()
    {
        await using var scope = database.CreateScope();
        var teamId = await CreateTeamAsync(scope);
        var technicianId = await CreateTechnicianAsync(scope);
        var teams = scope.ServiceProvider.GetRequiredService<TeamService>();

        var outcome = await teams.AddMemberAsync(teamId, technicianId, Cancellation);

        Assert.Equal(teamId, Assert.IsType<AddTeamMemberOutcome.Added>(outcome).Technician.TeamId);
        Assert.Equal(technicianId, Assert.Single((await teams.ListMembersAsync(teamId, Cancellation))!).Id);
    }

    [Fact]
    public async Task AddMember_rejects_a_missing_team()
    {
        await using var scope = database.CreateScope();
        var technicianId = await CreateTechnicianAsync(scope);
        var teams = scope.ServiceProvider.GetRequiredService<TeamService>();

        Assert.IsType<AddTeamMemberOutcome.TeamNotFound>(await teams.AddMemberAsync(Guid.NewGuid(), technicianId, Cancellation));
    }

    [Fact]
    public async Task AddMember_rejects_a_missing_technician()
    {
        await using var scope = database.CreateScope();
        var teamId = await CreateTeamAsync(scope);
        var teams = scope.ServiceProvider.GetRequiredService<TeamService>();

        Assert.IsType<AddTeamMemberOutcome.TechnicianNotFound>(await teams.AddMemberAsync(teamId, Guid.NewGuid(), Cancellation));
    }

    [Fact]
    public async Task AddMember_rejects_a_technician_who_belongs_to_another_team()
    {
        await using var scope = database.CreateScope();
        var firstTeamId = await CreateTeamAsync(scope);
        var secondTeamId = await CreateTeamAsync(scope);
        var technicianId = await CreateTechnicianAsync(scope);
        var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
        Assert.IsType<AddTeamMemberOutcome.Added>(await teams.AddMemberAsync(firstTeamId, technicianId, Cancellation));

        var outcome = await teams.AddMemberAsync(secondTeamId, technicianId, Cancellation);

        Assert.Equal(firstTeamId, Assert.IsType<AddTeamMemberOutcome.MemberOfAnotherTeam>(outcome).CurrentTeamId);
        Assert.Empty((await teams.ListMembersAsync(secondTeamId, Cancellation))!);
    }

    [Fact]
    public async Task AddMember_detects_a_concurrent_assignment_to_another_team_instead_of_moving_the_technician()
    {
        Guid firstTeamId, secondTeamId, technicianId;
        await using (var setup = database.CreateScope())
        {
            firstTeamId = await CreateTeamAsync(setup);
            secondTeamId = await CreateTeamAsync(setup);
            technicianId = await CreateTechnicianAsync(setup);
        }

        // The "slow" request has already loaded the technician (no team yet) before the "fast" request commits.
        await using var slow = database.CreateScope();
        await slow.ServiceProvider.GetRequiredService<IWorkforceDbContext>().Technicians
            .SingleAsync(technician => technician.Id == technicianId, Cancellation);

        await using (var fast = database.CreateScope())
        {
            Assert.IsType<AddTeamMemberOutcome.Added>(
                await fast.ServiceProvider.GetRequiredService<TeamService>().AddMemberAsync(firstTeamId, technicianId, Cancellation));
        }

        var outcome = await slow.ServiceProvider.GetRequiredService<TeamService>().AddMemberAsync(secondTeamId, technicianId, Cancellation);

        Assert.Equal(firstTeamId, Assert.IsType<AddTeamMemberOutcome.MemberOfAnotherTeam>(outcome).CurrentTeamId);
    }

    [Fact]
    public async Task RemoveMember_removes_the_technician_from_the_team_and_is_idempotent()
    {
        await using var scope = database.CreateScope();
        var teamId = await CreateTeamAsync(scope);
        var technicianId = await CreateTechnicianAsync(scope);
        var teams = scope.ServiceProvider.GetRequiredService<TeamService>();
        Assert.IsType<AddTeamMemberOutcome.Added>(await teams.AddMemberAsync(teamId, technicianId, Cancellation));

        Assert.IsType<RemoveTeamMemberOutcome.Removed>(await teams.RemoveMemberAsync(teamId, technicianId, Cancellation));
        Assert.IsType<RemoveTeamMemberOutcome.Removed>(await teams.RemoveMemberAsync(teamId, technicianId, Cancellation));

        Assert.Empty((await teams.ListMembersAsync(teamId, Cancellation))!);
    }

    private static async Task<Guid> CreateTeamAsync(AsyncServiceScope scope)
    {
        var outcome = await scope.ServiceProvider.GetRequiredService<TeamService>()
            .CreateAsync(new CreateTeam($"Team {Guid.NewGuid():N}", null), Cancellation);
        return Assert.IsType<CreateTeamOutcome.Created>(outcome).Team.Id;
    }

    private static async Task<Guid> CreateTechnicianAsync(AsyncServiceScope scope)
    {
        var outcome = await scope.ServiceProvider.GetRequiredService<TechnicianService>()
            .CreateAsync(new CreateTechnician("Technician", $"tech-{Guid.NewGuid():N}@crewcall.test", null), Cancellation);
        return Assert.IsType<CreateTechnicianOutcome.Created>(outcome).Technician.Id;
    }
}
