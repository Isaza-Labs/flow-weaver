using flow_weaver_backend.Services.Ai.Skills;

namespace flow_weaver_backend.Tests;

// The on-demand topic skills: which shipped skills they are, when a message or a
// tool call puts one in play, and how the model refers to one.
public class TopicSkillCatalogTests
{
    // The skills that stay in every turn. Everything else that ships has to be a
    // topic, so a new Skills/*.md is a conscious choice instead of a silent 10k
    // tokens on every round-trip.
    private static readonly string[] EveryTurn = ["base.md", "clarification.md", "discovery.md"];

    private static string ShippedSkillsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "flow_weaver_backend", "Skills");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("flow_weaver_backend/Skills not found above the test output");
    }

    private static List<TopicSkill> Topics(params string[] names)
        => names.Select(n => TopicSkillCatalog.From(n, "text of " + n)!).ToList();

    [Fact]
    public void EveryShippedSkillIsEitherATopicOrDeliberatelyEveryTurn()
    {
        var shipped = Directory.GetFiles(ShippedSkillsDir(), "*.md").Select(Path.GetFileName).ToList();

        Assert.DoesNotContain(shipped, n => !TopicSkillCatalog.IsTopic(n!) && !EveryTurn.Contains(n));
        Assert.All(EveryTurn, n => Assert.False(TopicSkillCatalog.IsTopic(n)));
    }

    [Fact]
    public void EveryTopicNamesAShippedSkill()
    {
        var shipped = Directory.GetFiles(ShippedSkillsDir(), "*.md").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "workflows.md", "git.md", "ssh.md", "email.md", "mcp.md", "triggers.md" })
            Assert.Contains(name, shipped);
    }

    [Fact]
    public void RowsThatAreNotTopicsStayOutOfTheCatalog()
    {
        Assert.Null(TopicSkillCatalog.From("base.md", "x"));
        Assert.Null(TopicSkillCatalog.From("my-house-style.md", "x"));
        Assert.NotNull(TopicSkillCatalog.From("Workflows.MD", "x"));
    }

    [Theory]
    [InlineData("crea un workflow que haga backup", "workflows.md")]
    [InlineData("necesito automatizar el backup", "workflows.md")]
    [InlineData("haz commit en el repo", "git.md")]
    [InlineData("envía un correo al equipo", "email.md")]
    [InlineData("¿qué pasó con la última ejecución?", "runs.md")]
    [InlineData("necesito una aprobación para el plan", "governance.md")]
    [InlineData("conéctate por SSH al router", "ssh.md")]
    [InlineData("programar el workflow cada día", "triggers.md")]
    public void MessagesNamingATopicPutItInPlay(string message, string topic)
    {
        var topics = Topics("workflows.md", "git.md", "email.md", "runs.md", "governance.md", "ssh.md", "triggers.md");

        Assert.Contains(TopicSkillCatalog.LoadKeyFor(topic), TopicSkillCatalog.MatchMentions(message, topics));
    }

    // Short keywords must not fire inside longer unrelated words.
    [Theory]
    [InlineData("send me the weekly report", "git.md")]          // "repo" in "report"
    [InlineData("the client is slow", "ssh.md")]                 // "cli" in "client"
    [InlineData("quiero el control de versiones del informe", "governance.md")] // "rol" in "control"
    [InlineData("who am I logged in as?", "workflows.md")]
    public void UnrelatedMessagesLeaveTheTopicOut(string message, string topic)
    {
        var topics = Topics("git.md", "ssh.md", "governance.md", "workflows.md");

        Assert.DoesNotContain(TopicSkillCatalog.LoadKeyFor(topic), TopicSkillCatalog.MatchMentions(message, topics));
    }

    [Fact]
    public void ATopicsToolsPutItInPlay()
    {
        var topics = Topics("git.md", "workflows.md", "mcp.md");

        Assert.Equal(["git.md"], TopicSkillCatalog.ForTool("git_commit_push", topics).Select(t => t.Name));
        Assert.Equal(["workflows.md"], TopicSkillCatalog.ForTool("create_workflow_plan", topics).Select(t => t.Name));
        Assert.Empty(TopicSkillCatalog.ForTool("list_workflows", topics));
    }

    // The ways the skills refer to one another all reach the same topic.
    [Theory]
    [InlineData("workflows.md")]
    [InlineData("workflows")]
    [InlineData("Skills/workflows.md")]
    [InlineData("WORKFLOWS")]
    public void FindAcceptsEveryWayASkillIsReferredTo(string name)
        => Assert.Equal("workflows.md", TopicSkillCatalog.Find(name, Topics("workflows.md"))?.Name);

    [Fact]
    public void LoadKeysAreStableAndDistinct()
    {
        Assert.Equal(TopicSkillCatalog.LoadKeyFor("git.md"), TopicSkillCatalog.LoadKeyFor("GIT.md"));
        Assert.NotEqual(TopicSkillCatalog.LoadKeyFor("git.md"), TopicSkillCatalog.LoadKeyFor("ssh.md"));
    }

    [Fact]
    public void RenderIndexesEveryTopicAndCarriesOnlyTheLoadedText()
    {
        var topics = Topics("git.md", "ssh.md");
        var loaded = new HashSet<Guid> { TopicSkillCatalog.LoadKeyFor("git.md") };

        var text = TopicSkillCatalog.Render(topics, loaded);

        Assert.Contains("`git.md`", text);
        Assert.Contains("`ssh.md`", text);
        Assert.Contains("text of git.md", text);
        Assert.DoesNotContain("text of ssh.md", text);
        Assert.Equal(string.Empty, TopicSkillCatalog.Render([], loaded));
    }
}
