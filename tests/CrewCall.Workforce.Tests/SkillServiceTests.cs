using CrewCall.Workforce.Skills;
using CrewCall.Workforce.Technicians;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrewCall.Workforce.Tests;

public sealed class SkillServiceTests(WorkforceDatabase database)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static string UniqueCode() => $"SK-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Create_saves_a_valid_skill_as_active_with_an_upper_case_code()
    {
        await using var scope = database.CreateScope();
        var skills = scope.ServiceProvider.GetRequiredService<SkillService>();
        var code = UniqueCode();

        var outcome = await skills.CreateAsync(new CreateSkill("  Fiber splicing  ", code.ToLowerInvariant(), null), Cancellation);

        var created = Assert.IsType<CreateSkillOutcome.Created>(outcome);
        Assert.Equal("Fiber splicing", created.Skill.Name);
        Assert.Equal(code.ToUpperInvariant(), created.Skill.Code);
        Assert.True(created.Skill.IsActive);
    }

    [Fact]
    public async Task Create_accepts_several_skills_without_a_code()
    {
        await using var scope = database.CreateScope();
        var skills = scope.ServiceProvider.GetRequiredService<SkillService>();

        Assert.IsType<CreateSkillOutcome.Created>(await skills.CreateAsync(new CreateSkill("Diagnostics", null, null), Cancellation));
        Assert.IsType<CreateSkillOutcome.Created>(await skills.CreateAsync(new CreateSkill("Networking", "  ", null), Cancellation));
    }

    [Fact]
    public async Task Create_rejects_an_empty_name()
    {
        await using var scope = database.CreateScope();
        var skills = scope.ServiceProvider.GetRequiredService<SkillService>();

        var outcome = await skills.CreateAsync(new CreateSkill("   ", null, null), Cancellation);

        Assert.Contains("name", Assert.IsType<CreateSkillOutcome.Invalid>(outcome).Errors.Keys);
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_code_regardless_of_case()
    {
        await using var scope = database.CreateScope();
        var skills = scope.ServiceProvider.GetRequiredService<SkillService>();
        var code = UniqueCode();
        Assert.IsType<CreateSkillOutcome.Created>(await skills.CreateAsync(new CreateSkill("HVAC", code, null), Cancellation));

        var outcome = await skills.CreateAsync(new CreateSkill("HVAC again", code.ToLowerInvariant(), null), Cancellation);

        Assert.Equal(code.ToUpperInvariant(), Assert.IsType<CreateSkillOutcome.CodeAlreadyExists>(outcome).Code);
    }

    [Fact]
    public async Task Assign_links_an_existing_skill_to_an_existing_technician()
    {
        await using var scope = database.CreateScope();
        var technicianId = await CreateTechnicianAsync(scope);
        var skillId = await CreateSkillAsync(scope);
        var skills = scope.ServiceProvider.GetRequiredService<SkillService>();

        var outcome = await skills.AssignToTechnicianAsync(technicianId, skillId, Cancellation);

        Assert.Equal(skillId, Assert.IsType<AssignSkillOutcome.Assigned>(outcome).Skill.Id);
        var technicianSkills = await skills.ListForTechnicianAsync(technicianId, Cancellation);
        Assert.NotNull(technicianSkills);
        Assert.Equal(skillId, Assert.Single(technicianSkills).Id);
    }

    [Fact]
    public async Task Assign_rejects_a_missing_technician()
    {
        await using var scope = database.CreateScope();
        var skillId = await CreateSkillAsync(scope);
        var skills = scope.ServiceProvider.GetRequiredService<SkillService>();

        var outcome = await skills.AssignToTechnicianAsync(Guid.NewGuid(), skillId, Cancellation);

        Assert.IsType<AssignSkillOutcome.TechnicianNotFound>(outcome);
    }

    [Fact]
    public async Task Assign_rejects_a_missing_skill()
    {
        await using var scope = database.CreateScope();
        var technicianId = await CreateTechnicianAsync(scope);
        var skills = scope.ServiceProvider.GetRequiredService<SkillService>();

        var outcome = await skills.AssignToTechnicianAsync(technicianId, Guid.NewGuid(), Cancellation);

        Assert.IsType<AssignSkillOutcome.SkillNotFound>(outcome);
    }

    [Fact]
    public async Task Assigning_the_same_skill_again_is_idempotent_and_creates_no_duplicate()
    {
        await using var scope = database.CreateScope();
        var technicianId = await CreateTechnicianAsync(scope);
        var skillId = await CreateSkillAsync(scope);
        var skills = scope.ServiceProvider.GetRequiredService<SkillService>();
        Assert.IsType<AssignSkillOutcome.Assigned>(await skills.AssignToTechnicianAsync(technicianId, skillId, Cancellation));

        var outcome = await skills.AssignToTechnicianAsync(technicianId, skillId, Cancellation);

        Assert.IsType<AssignSkillOutcome.AlreadyAssigned>(outcome);
        Assert.Single((await skills.ListForTechnicianAsync(technicianId, Cancellation))!);
    }

    [Fact]
    public async Task Remove_unlinks_the_skill_and_is_idempotent()
    {
        await using var scope = database.CreateScope();
        var technicianId = await CreateTechnicianAsync(scope);
        var skillId = await CreateSkillAsync(scope);
        var skills = scope.ServiceProvider.GetRequiredService<SkillService>();
        Assert.IsType<AssignSkillOutcome.Assigned>(await skills.AssignToTechnicianAsync(technicianId, skillId, Cancellation));

        Assert.IsType<RemoveSkillOutcome.Removed>(await skills.RemoveFromTechnicianAsync(technicianId, skillId, Cancellation));
        Assert.IsType<RemoveSkillOutcome.Removed>(await skills.RemoveFromTechnicianAsync(technicianId, skillId, Cancellation));

        Assert.Empty((await skills.ListForTechnicianAsync(technicianId, Cancellation))!);
    }

    private static async Task<Guid> CreateTechnicianAsync(AsyncServiceScope scope)
    {
        var outcome = await scope.ServiceProvider.GetRequiredService<TechnicianService>()
            .CreateAsync(new CreateTechnician("Technician", $"tech-{Guid.NewGuid():N}@crewcall.test", null), Cancellation);
        return Assert.IsType<CreateTechnicianOutcome.Created>(outcome).Technician.Id;
    }

    private static async Task<Guid> CreateSkillAsync(AsyncServiceScope scope)
    {
        var outcome = await scope.ServiceProvider.GetRequiredService<SkillService>()
            .CreateAsync(new CreateSkill("Electrical", UniqueCode(), null), Cancellation);
        return Assert.IsType<CreateSkillOutcome.Created>(outcome).Skill.Id;
    }
}
