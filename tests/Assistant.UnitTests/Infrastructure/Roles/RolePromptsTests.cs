using Assistant.Application.Health;
using Assistant.Domain.Health;
using Assistant.Infrastructure.Roles;

namespace Assistant.UnitTests.Infrastructure.Roles;

public class RolePromptsTests
{
    [Fact]
    public void Health_prompt_files_are_embedded_in_the_infrastructure_assembly()
    {
        var prompts = new RolePrompts(typeof(RolePrompts).Assembly);

        prompts.Find("health", "prompt.md").ShouldNotBeNullOrWhiteSpace();
        prompts.Find("health", "extract.md").ShouldNotBeNullOrWhiteSpace();
        prompts.Missing.ShouldBeEmpty();
    }

    [Fact]
    public void Extraction_prompt_describes_the_json_contract()
    {
        var prompts = new RolePrompts(typeof(RolePrompts).Assembly);

        var text = prompts.Find("health", "extract.md");

        text.ShouldNotBeNull();
        text.ShouldContain("\"events\"");
        text.ShouldContain("\"unclear\"");
        text.ShouldContain("\"is_question\"");
        foreach (var value in HealthEventTypes.All.Concat(GlucoseContexts.All).Concat(SymptomCodes.All))
        {
            text.ShouldContain(value);
        }
    }

    [Fact]
    public void Role_lookup_trims_and_ignores_case()
    {
        var prompts = new RolePrompts(typeof(RolePrompts).Assembly);

        var padded = prompts.Find(" Health ", "prompt.md");

        padded.ShouldNotBeNull();
        padded.ShouldBe(prompts.Find("health", "prompt.md"));
    }

    [Fact]
    public void Unknown_role_or_file_returns_null()
    {
        var prompts = new RolePrompts(typeof(RolePrompts).Assembly);

        prompts.Find("unknown", "prompt.md").ShouldBeNull();
        prompts.Find("health", "unknown.md").ShouldBeNull();
    }

    [Fact]
    public void An_assembly_without_role_resources_reports_both_files_missing()
    {
        var prompts = new RolePrompts(typeof(RolePromptsTests).Assembly);

        prompts.Missing.ShouldBe(new[] { "roles/health/prompt.md", "roles/health/extract.md" });
        prompts.Find("health", "prompt.md").ShouldBeNull();
    }
}
