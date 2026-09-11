using System.Text.Json;
using flow_weaver_backend.Services.Engine;

namespace flow_weaver_backend.Tests;

// The template filter pipeline: `{{ steps.x.output.stdout | strip | truncate(40) }}`.
// Two rules carry the weight — an unresolved path must leave the literal
// template in place (so the operator SEES the typo instead of silently getting
// an empty string), and `default` is the only filter that can rescue one.
public class VariableResolverFilterTests
{
    private static VariableResolver.ParsedExpression Parse(string raw)
        => VariableResolver.ParseExpression(raw);

    private static JsonElement? ApplyFilters(JsonElement? value, params (string Name, string[] Args)[] filters)
        => VariableResolver.ApplyFilters(
            value,
            filters.Select(f => new VariableResolver.FilterCall(f.Name, f.Args)).ToList());

    private static JsonElement Json(string json) => TestJson.Element(json);

    // ─── ParseExpression ────────────────────────────────────────────────

    [Fact]
    public void Parse_BarePathHasNoFilters()
    {
        var parsed = Parse("steps.n1.output.stdout");

        Assert.Equal("steps.n1.output.stdout", parsed.Path);
        Assert.Empty(parsed.Filters);
    }

    [Fact]
    public void Parse_TrimsWhitespaceAroundThePath()
    {
        Assert.Equal("device.name", Parse("  device.name  ").Path);
    }

    [Fact]
    public void Parse_ReadsAFilterChain()
    {
        var parsed = Parse("steps.n1.output | strip | upper");

        Assert.Equal("steps.n1.output", parsed.Path);
        Assert.Equal(new[] { "strip", "upper" }, parsed.Filters.Select(f => f.Name));
    }

    [Fact]
    public void Parse_ReadsFilterArguments()
    {
        var parsed = Parse("x | truncate(40)");

        var filter = Assert.Single(parsed.Filters);
        Assert.Equal("truncate", filter.Name);
        Assert.Equal("40", Assert.Single(filter.Args));
    }

    // Quoted arguments may contain commas and pipes — splitting naively would
    // mangle a default value like "n/a, unknown".
    [Fact]
    public void Parse_QuotedArgumentsKeepCommas()
    {
        var parsed = Parse("""x | default('n/a, unknown')""");

        Assert.Equal("n/a, unknown", Assert.Single(Assert.Single(parsed.Filters).Args));
    }

    [Fact]
    public void Parse_ReadsSeveralArguments()
    {
        var parsed = Parse("x | thing(1, 'two')");

        Assert.Equal(new[] { "1", "two" }, Assert.Single(parsed.Filters).Args);
    }

    [Theory]
    [InlineData("\"quoted\"", "quoted")]
    [InlineData("'quoted'", "quoted")]
    public void Parse_ArgumentsAreUnquoted(string raw, string expected)
    {
        var parsed = Parse($"x | default({raw})");

        Assert.Equal(expected, Assert.Single(Assert.Single(parsed.Filters).Args));
    }

    // A malformed filter call keeps its whole token as the name, so the
    // residual-template scan can flag it as a typo rather than throwing.
    [Theory]
    [InlineData("x | truncate(40")]
    [InlineData("x | truncate)40(")]
    public void Parse_MalformedFilterCallDoesNotThrow(string raw)
    {
        var parsed = Parse(raw);

        Assert.Single(parsed.Filters);
    }

    [Fact]
    public void Parse_EmptyFilterSegmentsAreDropped()
    {
        Assert.Single(Parse("x | | upper").Filters);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyExpressionYieldsEmptyPathAndNoFilters(string raw)
    {
        var parsed = Parse(raw);

        Assert.Empty(parsed.Path);
        Assert.Empty(parsed.Filters);
    }

    // ─── string filters ─────────────────────────────────────────────────

    [Fact]
    public void Trim_RemovesSurroundingWhitespace()
    {
        var result = ApplyFilters(Json("\"  padded  \""), ("trim", Array.Empty<string>()));

        Assert.Equal("padded", result!.Value.GetString());
    }

    [Fact]
    public void UpperAndLower_ChangeCase()
    {
        Assert.Equal("ABC", ApplyFilters(Json("\"aBc\""), ("upper", Array.Empty<string>()))!.Value.GetString());
        Assert.Equal("abc", ApplyFilters(Json("\"aBc\""), ("lower", Array.Empty<string>()))!.Value.GetString());
    }

    [Fact]
    public void Truncate_CutsToTheGivenLength()
    {
        var result = ApplyFilters(Json("\"abcdefghij\""), ("truncate", new[] { "4" }));

        Assert.Equal("abcd", result!.Value.GetString());
    }

    // Without an argument the filter falls back to its documented default
    // rather than truncating to zero.
    [Fact]
    public void Truncate_WithoutAnArgumentUsesTheDefaultLimit()
    {
        var long300 = new string('x', 300);
        var result = ApplyFilters(Json($"\"{long300}\""), ("truncate", Array.Empty<string>()));

        Assert.Equal(200, result!.Value.GetString()!.Length);
    }

    [Fact]
    public void Truncate_ShorterValuesAreUnchanged()
    {
        Assert.Equal("abc", ApplyFilters(Json("\"abc\""), ("truncate", new[] { "10" }))!.Value.GetString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Truncate_NonPositiveLimitYieldsEmpty(string limit)
    {
        Assert.Equal("", ApplyFilters(Json("\"abcdef\""), ("truncate", new[] { limit }))!.Value.GetString());
    }

    [Fact]
    public void Truncate_NonNumericArgumentFallsBackToTheDefault()
    {
        var long300 = new string('x', 300);
        var result = ApplyFilters(Json($"\"{long300}\""), ("truncate", new[] { "lots" }));

        Assert.Equal(200, result!.Value.GetString()!.Length);
    }

    // Device output routinely carries ANSI escapes; leaving them in corrupts
    // reports and CSVs downstream.
    [Fact]
    public void StripAnsi_RemovesEscapeSequences()
    {
        var result = ApplyFilters(Json("\"\\u001b[31mred\\u001b[0m\""), ("strip_ansi", Array.Empty<string>()));

        Assert.Equal("red", result!.Value.GetString());
    }

    [Fact]
    public void Strip_RemovesAnsiControlCharsAndTrims()
    {
        var result = ApplyFilters(Json("\"  \\u001b[31mred\\u0007  \""), ("strip", Array.Empty<string>()));

        Assert.Equal("red", result!.Value.GetString());
    }

    // `json` renders the value as a JSON string, so a template can embed a
    // whole object into a text field.
    [Fact]
    public void Json_SerialisesTheValueAsAString()
    {
        var result = ApplyFilters(Json("""{"a":1}"""), ("json", Array.Empty<string>()));

        Assert.Equal(JsonValueKind.String, result!.Value.ValueKind);
        Assert.Equal("""{"a":1}""", result.Value.GetString());
    }

    // A text filter applied to something that is not text PASSES IT THROUGH.
    //
    // This used to stringify first, on the reasoning that
    // `{{ steps.x.output.count | upper }}` should not explode — which pass-through also
    // satisfies, without the cost. Stringifying made the expression yield the STRING "5" where
    // the author still had the number 5, so every downstream numeric comparison silently
    // compared text. `templates.filter.non_string_passes_through` is the vector that found it.
    [Fact]
    public void StringFiltersPassNonStringValuesThroughUnchanged()
    {
        Assert.Equal(JsonValueKind.Number, ApplyFilters(Json("42"), ("trim", Array.Empty<string>()))!.Value.ValueKind);
        Assert.Equal(42, ApplyFilters(Json("42"), ("trim", Array.Empty<string>()))!.Value.GetInt32());
        Assert.Equal(JsonValueKind.True, ApplyFilters(Json("true"), ("upper", Array.Empty<string>()))!.Value.ValueKind);
    }

    // ─── the `default` filter (the only rescue) ─────────────────────────

    [Fact]
    public void Default_SuppliesAValueWhenThePathDidNotResolve()
    {
        var result = ApplyFilters(null, ("default", new[] { "n/a" }));

        Assert.Equal("n/a", result!.Value.GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public void Default_AlsoRescuesNullAndEmptyValues(string json)
    {
        var result = ApplyFilters(Json(json), ("default", new[] { "fallback" }));

        Assert.Equal("fallback", result!.Value.GetString());
    }

    [Fact]
    public void Default_LeavesARealValueAlone()
    {
        Assert.Equal("real", ApplyFilters(Json("\"real\""), ("default", new[] { "fallback" }))!.Value.GetString());
    }

    // A falsy-but-present value is still a value — 0 and false must not be
    // replaced by the default.
    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    public void Default_DoesNotReplaceFalsyButPresentValues(string json)
    {
        var result = ApplyFilters(Json(json), ("default", new[] { "fallback" }));

        Assert.Equal(json, result!.Value.GetRawText());
    }

    [Fact]
    public void Default_WithoutAnArgumentYieldsAnEmptyString()
    {
        Assert.Equal("", ApplyFilters(null, ("default", Array.Empty<string>()))!.Value.GetString());
    }

    // ─── unresolved values short-circuit ────────────────────────────────

    // Every filter except `default` gives up on an unresolved path, and the
    // caller keeps the literal template so the typo is visible.
    [Theory]
    [InlineData("trim")]
    [InlineData("upper")]
    [InlineData("truncate")]
    [InlineData("json")]
    [InlineData("strip")]
    public void NonDefaultFiltersReturnNullForAnUnresolvedPath(string filter)
    {
        Assert.Null(ApplyFilters(null, (filter, Array.Empty<string>())));
    }

    // `default` before another filter rescues the chain...
    [Fact]
    public void DefaultEarlyInTheChainRescuesTheRest()
    {
        var result = ApplyFilters(null, ("default", new[] { "n/a" }), ("upper", Array.Empty<string>()));

        Assert.Equal("N/A", result!.Value.GetString());
    }

    // ...but after one, the chain has already bailed out.
    [Fact]
    public void DefaultAfterAFailedFilterCannotRescue()
    {
        Assert.Null(ApplyFilters(null, ("upper", Array.Empty<string>()), ("default", new[] { "n/a" })));
    }

    // An unknown filter is a no-op — the residual scan reports it as a typo
    // rather than the run failing mid-step.
    [Fact]
    public void UnknownFiltersAreIgnored()
    {
        var result = ApplyFilters(Json("\"value\""), ("nosuchfilter", Array.Empty<string>()));

        Assert.Equal("value", result!.Value.GetString());
    }

    [Fact]
    public void FiltersApplyInOrder()
    {
        var result = ApplyFilters(
            Json("\"  hello world  \""),
            ("trim", Array.Empty<string>()),
            ("upper", Array.Empty<string>()),
            ("truncate", new[] { "5" }));

        Assert.Equal("HELLO", result!.Value.GetString());
    }

    [Fact]
    public void FilterNamesAreCaseInsensitive()
    {
        Assert.Equal("ABC", ApplyFilters(Json("\"abc\""), ("UPPER", Array.Empty<string>()))!.Value.GetString());
    }

    // ─── Stringify (shared with ConditionEvaluator) ─────────────────────

    // ConditionEvaluator compares against these exact strings, so a
    // stringified `true` vs `"true"` divergence would flip `== "completed"`
    // style conditions.
    [Theory]
    [InlineData("\"text\"", "text")]
    [InlineData("42", "42")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    public void Stringify_ProducesTheSharedStringForm(string json, string expected)
    {
        Assert.Equal(expected, VariableResolver.Stringify(Json(json)));
    }

    // A JSON null contributes NOTHING to a surrounding string.
    //
    // It used to stringify to the literal text "null", pinned on the reasoning that a
    // condition comparing against "" would then not match a null. That reasoning no longer
    // applies: conditions are evaluated by ConditionEvaluator, which resolves and compares
    // values without routing them through here. What remained was inline substitution putting
    // the word "null" into the middle of a command sent to a device.
    // `templates.substitution.inline_null_is_empty_string` is the vector that found it.
    [Fact]
    public void Stringify_NullContributesNothing()
    {
        Assert.Equal(string.Empty, VariableResolver.Stringify(Json("null")));
    }

    [Fact]
    public void Stringify_ObjectsAndArraysKeepTheirRawJson()
    {
        Assert.Equal("""{"a":1}""", VariableResolver.Stringify(Json("""{"a":1}""")));
        Assert.Equal("[1,2]", VariableResolver.Stringify(Json("[1,2]")));
    }
}
