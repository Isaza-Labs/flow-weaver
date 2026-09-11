using System.Text.Json;
using flow_weaver_backend.Services.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// What a structural rejection SAYS, against the real validator and the real embedded
// schema — not a fake.
//
// The `Errors` list reaches clients verbatim as the `details` array of the
// `400 schema_invalid` body, so it is a public contract with no version on it. These tests
// pin it, and they were written and made green BEFORE the addressable errors were added:
// that they still pass unchanged is the evidence that the addition is additive rather than
// a reshape. A test here that needs editing means something a client can see has moved.
public class SchemaValidationErrorShapeTests
{
    // `SharedSchemaValidator.Instance`, not a new one, and it is not an optimisation.
    //
    // `JsonSchema.FromText` registers the document by its `$id` in a PROCESS-WIDE registry,
    // and a second registration of the same id throws "Overwriting registered schemas is not
    // permitted". Production never notices — the validator is a Singleton, built once — so
    // the failure shows up only in tests, and only in a FULL run: a filtered run of one class
    // constructs one instance and passes. The first draft of this file newed one up per test
    // method and then per class, and hit it both times.
    //
    // Worth remembering if the two products' schema files are ever unified: they coexist
    // today only because their `$id`s differ.
    private static WorkflowSchemaValidator Validator() => SharedSchemaValidator.Instance;

    private static (JsonElement Nodes, JsonElement Edges) Graph(string nodes, string edges)
    {
        using var n = JsonDocument.Parse(nodes);
        using var e = JsonDocument.Parse(edges);
        return (n.RootElement.Clone(), e.RootElement.Clone());
    }

    private static WorkflowValidationResult Validate(string nodes, string edges = "[]")
    {
        var (n, e) = Graph(nodes, edges);
        return Validator().Validate(n, e);
    }

    // ── the shape clients already depend on ──────────────────────────────

    [Fact]
    public void A_valid_graph_reports_valid_with_no_errors()
    {
        var r = Validate("""[{"id":"a","snippet_id":"__start__"}]""");

        Assert.True(r.IsValid);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void A_missing_required_member_is_reported_as_a_pointer_and_a_message()
    {
        var r = Validate("""[{"id":"a"}]""");

        Assert.False(r.IsValid);
        var only = Assert.Single(r.Errors);
        // The format the `details` array carries today: "<json pointer>: <message>".
        Assert.StartsWith("/nodes/0: ", only, StringComparison.Ordinal);
        Assert.Contains("snippet_id", only, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexpected_member_is_reported_against_the_node_that_carries_it()
    {
        var r = Validate("""[{"id":"a","snippet_id":"__start__","nope":1}]""");

        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.StartsWith("/nodes/0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_wrong_member_type_on_an_edge_is_reported_against_that_edge()
    {
        var r = Validate(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"__end__"}]""",
            """[{"source":"a","target":"b","type":"not_a_type"}]""");

        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.StartsWith("/edges/0", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_violations_are_all_reported_rather_than_only_the_first()
    {
        var r = Validate("""[{"id":"a"},{"id":"b"}]""");

        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.StartsWith("/nodes/0", StringComparison.Ordinal));
        Assert.Contains(r.Errors, e => e.StartsWith("/nodes/1", StringComparison.Ordinal));
    }

    // A Guid `snippet_id` satisfies one branch of the anyOf and fails the other. The
    // validator only descends into invalid parents for exactly this reason, and a
    // regression there floods `details` with phantom enum failures on every valid graph.
    [Fact]
    public void A_uuid_snippet_id_produces_no_phantom_errors_from_the_other_anyOf_branch()
    {
        var r = Validate($$"""[{"id":"a","snippet_id":"{{Guid.NewGuid()}}"}]""");

        Assert.True(r.IsValid);
        Assert.Empty(r.Errors);
    }

    // ── the addressable form, beside the messages ────────────────────────

    [Fact]
    public void A_valid_graph_carries_no_violations()
    {
        var r = Validate("""[{"id":"a","snippet_id":"__start__"}]""");

        Assert.True(r.IsValid);
        // Null rather than empty: "valid" must never be something a reader infers from an
        // empty list that might equally mean "not checked".
        Assert.Null(r.Violations);
    }

    [Fact]
    public void A_missing_required_member_names_the_rule_and_the_node()
    {
        var r = Validate("""[{"id":"a"}]""");

        var v = Assert.Single(r.Violations!);
        Assert.Equal("required", v.Keyword);
        Assert.Equal("/nodes/0", v.Path);
        Assert.Contains("snippet_id", v.Message, StringComparison.Ordinal);
    }

    // The location is always there; the keyword is not always. JsonSchema.Net reports an
    // `additionalProperties: false` rejection as an unnamed failure against the member
    // itself — keyword empty, message "All values fail against the false schema", pointer
    // `/nodes/0/nope`. That is a property of the evaluator, and inventing "additionalProperties"
    // here to make the shape look uniform would be fabricating a rule name from a message.
    //
    // Which is fine, because the pointer is the actionable half: it names the member to mark.
    [Fact]
    public void An_unexpected_member_is_addressed_even_where_the_evaluator_names_no_rule()
    {
        var r = Validate("""[{"id":"a","snippet_id":"__start__","nope":1}]""");

        var v = Assert.Single(r.Violations!);
        Assert.Equal("/nodes/0/nope", v.Path);
        Assert.Equal(string.Empty, v.Keyword);
    }

    [Fact]
    public void A_wrong_member_type_names_the_edge_it_is_on()
    {
        var r = Validate(
            """[{"id":"a","snippet_id":"__start__"},{"id":"b","snippet_id":"__end__"}]""",
            """[{"source":"a","target":"b","type":"not_a_type"}]""");

        Assert.Contains(r.Violations!, v => v.Path.StartsWith("/edges/0", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_violation_is_addressed_separately()
    {
        var r = Validate("""[{"id":"a"},{"id":"b"}]""");

        Assert.Contains(r.Violations!, v => v.Path == "/nodes/0");
        Assert.Contains(r.Violations!, v => v.Path == "/nodes/1");
    }

    // The two lists come from one walk, so they cannot disagree about how many failures
    // there were. If they ever do, one of them is being built somewhere else.
    [Fact]
    public void The_messages_and_the_violations_describe_the_same_failures()
    {
        var r = Validate("""[{"id":"a"},{"id":"b","snippet_id":"__start__","nope":1}]""");

        Assert.Equal(r.Errors.Count, r.Violations!.Count);
        foreach (var v in r.Violations!)
            Assert.Contains(r.Errors, e => e.Contains(v.Message, StringComparison.Ordinal));
    }
}
