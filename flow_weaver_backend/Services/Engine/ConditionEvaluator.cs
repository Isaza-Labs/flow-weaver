using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace flow_weaver_backend.Services.Engine;

// The conformance gate found six vectors where this product's evaluator failed OPEN: an
// unresolvable reference, a malformed expression and an unbalanced parenthesis each yielded
// `true`, so a conditional edge fired and the workflow branched on data it could not read.
// Two more failed on missing grouping and on an operator inside a quoted literal being read
// as an operator.
//
// The reference evaluator already answered all eight, so this is a copy rather than a
// rewrite — the second file in the shared engine map, after WorkflowCanonicalizer. Keeping
// it identical is the point: a condition is either the same in every engine or a workflow
// means two different things depending on where it runs.
//
public interface IConditionEvaluator
{
    bool Evaluate(
        string expression,
        IReadOnlyDictionary<string, StepResult> completedSteps,
        JsonElement? deviceContext = null,
        JsonElement? runInput = null,
        JsonElement? runContext = null);
}

// Evaluates the `condition` on a conditional edge.
//
// Before this existed the executor treated `conditional` edges as plain success
// edges, so a workflow with a branch ran both arms. The schema has always allowed
// the condition string; nothing read it.
//
// Grammar, deliberately small:
//   {{ steps.ping.output.reachable }} == true
//   {{ steps.rest.output.status_code }} >= 200 && {{ steps.rest.output.status_code }} < 300
//   {{ steps.ssh.output.results[0].ok }} != false
//   {{ steps.transform.output.count }}          (bare value, truthiness)
//   {{ steps.ssh.output.stdout | strip | lower }} == "ok"
//
// References resolve through VariableResolver's expression grammar — path and
// filter chain alike — so a reference that works in a payload works here. Two
// grammars would let an author write a condition that reads correctly and
// evaluates against nothing.
//
// Fail-closed: an expression that cannot be evaluated returns false. A conditional
// edge exists to gate something; firing it because the gate was unparseable is the
// worse of the two failures.
public sealed partial class ConditionEvaluator : IConditionEvaluator
{
    [GeneratedRegex(@"\{\{\s*steps\.([\w-]+)\.output([^}]*?)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex StepRegex();

    [GeneratedRegex(@"\{\{\s*device(\b[^}]*?)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex DeviceRegex();

    [GeneratedRegex(@"\{\{\s*input(\b[^}]*?)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex InputRegex();

    [GeneratedRegex(@"\{\{\s*run(\b[^}]*?)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex RunRegex();

    // Every namespace in ONE pattern, so substitution is a single pass. The four
    // regexes above are kept because they document each namespace's shape, but the
    // evaluator no longer runs them in sequence — see Substitute.
    [GeneratedRegex(
        @"\{\{\s*(?:steps\.(?<step>[\w-]+)\.output(?<steppath>[^}]*?)|device(?<device>\b[^}]*?)|input(?<input>\b[^}]*?)|run(?<run>\b[^}]*?))\s*\}\}",
        RegexOptions.Compiled)]
    private static partial Regex AnyRegex();

    // Longest first: matching ">" before ">=" would split ">=" into ">" and "=".
    private static readonly string[] Operators = ["==", "!=", ">=", "<=", ">", "<"];

    private readonly ILogger<ConditionEvaluator> _logger;

    public ConditionEvaluator(ILogger<ConditionEvaluator> logger) => _logger = logger;

    public bool Evaluate(
        string expression,
        IReadOnlyDictionary<string, StepResult> completedSteps,
        JsonElement? deviceContext = null,
        JsonElement? runInput = null,
        JsonElement? runContext = null)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;

        try
        {
            var resolved = Substitute(expression, completedSteps, deviceContext, runInput, runContext);

            // Whole-expression check, not only per-fragment: in `(a || b` the split
            // yields a clean `b` whose truthiness would decide the OR, quietly
            // evaluating half of a malformed expression. Unbalanced overall = the
            // author wrote something this grammar cannot honour = closed.
            if (!WellFormed(resolved))
            {
                _logger.LogWarning("workflow.condition.unbalanced expression={Expression}", expression);
                return false;
            }

            return EvaluateBoolean(resolved);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workflow.condition.failed expression={Expression}", expression);
            return false;
        }
    }

    // Replaces each reference with its literal JSON text, so the comparison below
    // works on values rather than templates. An unresolved reference becomes the
    // sentinel `__unresolved__`, which compares equal to nothing and is falsy —
    // that is what makes an authoring typo fail the gate instead of opening it.
    private static string Substitute(
        string expression,
        IReadOnlyDictionary<string, StepResult> steps,
        JsonElement? device,
        JsonElement? input,
        JsonElement? run)
    {
        // ONE pass over the original expression. It used to be four passes, each run
        // over the output of the last, which meant a value a step produced was itself
        // scanned for references: a device whose command output or SSH banner contained
        // the literal text `{{ input.api_token }}` got that reference RESOLVED on the
        // next pass, and the branch was then steered by remote text. Regex.Replace does
        // not rescan what an evaluator returns, so a single alternation closes it.
        return AnyRegex().Replace(expression, m =>
        {
            if (m.Groups["step"].Success)
            {
                var root = steps.TryGetValue(m.Groups["step"].Value, out var step) ? step.Output : (JsonElement?)null;
                return Literal(VariableResolver.ResolveExpression(root, m.Groups["steppath"].Value));
            }
            if (m.Groups["device"].Success)
                return Literal(VariableResolver.ResolveExpression(device, m.Groups["device"].Value));
            if (m.Groups["input"].Success)
                return Literal(VariableResolver.ResolveExpression(input, m.Groups["input"].Value));
            return Literal(VariableResolver.ResolveExpression(run, m.Groups["run"].Value));
        });
    }

    private const string Unresolved = "__unresolved__";

    // Text values go in QUOTED. Splicing them bare let the value restructure the
    // expression that is about to parse it: a command whose output contains `||`, `==`
    // or a stray quote turned one comparison into two, or moved the operator. Quoting
    // makes the value one opaque operand — which is what it is — and the scanners below
    // treat a quoted region as inert. Numbers, booleans and the unresolved sentinel
    // stay bare: they are grammar, not data.
    private static string Literal(JsonElement? resolved) => resolved is not { } value ? Unresolved : value.ValueKind switch
    {
        JsonValueKind.String => Quote(value.GetString() ?? string.Empty),
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        JsonValueKind.Object or JsonValueKind.Array => Quote(value.GetRawText()),
        _ => value.ToString(),
    };

    private static string Quote(string raw) =>
        $"\"{raw.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    // `&&` binds tighter than `||`, as everywhere else. Both short-circuit.
    private bool EvaluateBoolean(string expression)
    {
        // Strip redundant outer parens FIRST, so a grouped sub-expression comes back
        // through this method instead of reaching EvaluateAtom as one opaque lump.
        // That is what makes `(a || b) && c` evaluate as grouped rather than fail.
        expression = Unwrap(expression);

        var orParts = SplitTop(expression, "||");
        if (orParts.Count > 1)
            return orParts.Any(EvaluateBoolean);

        var andParts = SplitTop(expression, "&&");
        if (andParts.Count > 1)
            return andParts.All(EvaluateBoolean);

        return EvaluateAtom(expression.Trim());
    }

    // Splits only at depth zero and outside quotes. `string.Split` cut through both:
    // `(a || b) && c` became `(a` and `b) && c` — two fragments this grammar cannot
    // evaluate — and `{{ x }} == "a||b"` split inside the literal, leaving the fragment
    // `b"`, a non-empty string, which is TRUTHY. That one failed OPEN: the edge fired.
    private static List<string> SplitTop(string expression, string separator)
    {
        var parts = new List<string>();
        var start = 0;
        int idx;
        while ((idx = IndexOfTop(expression, separator, start)) >= 0)
        {
            parts.Add(expression[start..idx]);
            start = idx + separator.Length;
        }
        parts.Add(expression[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    private bool EvaluateAtom(string atom)
    {
        atom = Unwrap(atom);

        // An unbalanced parenthesis here means the || / && split cut through a
        // parenthesized group — `(a || b) && c` splits into `(a` and `b) && c`,
        // fragments this grammar cannot evaluate. Before this check, `(a` fell
        // into bare-value truthiness and a non-empty string is truthy, so a
        // grouped expression could FIRE an edge it should have gated. Balanced
        // parens inside operands (a value like "(ok)") still compare normally.
        if (!WellFormed(atom)) return false;

        foreach (var op in Operators)
        {
            // Outside quotes: `{{ x }} > "a==b"` used to split on the `==` inside the
            // literal and never evaluate the `>` the author wrote.
            var idx = IndexOfTop(atom, op);
            if (idx < 0) continue;

            var left = atom[..idx].Trim();
            var right = atom[(idx + op.Length)..].Trim();

            // A missing operand is a parse error, and §9 says a parse error is false.
            // The old guard was `idx <= 0`, which SKIPPED an operator at position 0 and
            // let `== "x"` fall through to bare-value truthiness — a non-empty string,
            // so the malformed gate opened.
            if (left.Length == 0 || right.Length == 0) return false;

            return Compare(Unquote(left), Unquote(right), op);
        }

        // No operator: truthiness of the bare value.
        return Truthy(Unquote(atom));
    }

    // Strips one layer of balanced parentheses so `(a == b)` evaluates.
    private static string Unwrap(string s)
    {
        s = s.Trim();
        while (s.Length > 1 && s[0] == '(' && s[^1] == ')')
        {
            var depth = 0;
            var balanced = true;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                if (depth == 0 && i < s.Length - 1) { balanced = false; break; }
            }
            if (!balanced) break;
            s = s[1..^1].Trim();
        }
        return s;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length < 2) return s;
        var q = s[0];
        if ((q != '"' && q != '\'') || s[^1] != q) return s;

        // Single pass, so a value that legitimately contains a backslash before a quote
        // comes back exactly as the handler produced it. Chained Replace calls would
        // re-consume their own output and corrupt that case.
        var body = s[1..^1];
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\' && i + 1 < body.Length && (body[i + 1] == '\\' || body[i + 1] == q))
            {
                sb.Append(body[++i]);
                continue;
            }
            sb.Append(body[i]);
        }
        return sb.ToString();
    }

    // Scans left to right for `needle` at nesting depth zero and OUTSIDE any quoted
    // region, which is what separates a `||` the author wrote from one that arrived
    // inside a device's output. Returns -1 when there is none.
    private static int IndexOfTop(string s, string needle, int from = 0)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { if (depth > 0) depth--; continue; }
            if (depth == 0 && i >= from
                && i + needle.Length <= s.Length
                && string.CompareOrdinal(s, i, needle, 0, needle.Length) == 0)
                return i;
        }
        return -1;
    }

    // Balanced parentheses AND terminated quotes. §9: any parse error is false, and an
    // unterminated quote is a parse error that would otherwise swallow the rest of the
    // expression and decide the branch on whatever fragment survived.
    private static bool WellFormed(string s)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }
        return depth == 0 && quote == '\0';
    }

    private static bool Compare(string left, string right, string op)
    {
        // An unresolved side compares equal to nothing and orders after nothing:
        // the contract says an unresolved comparison is false, whichever operator.
        if (left == Unresolved || right == Unresolved) return false;

        // Numeric when both sides are numbers, so `10 > 9` is not string-compared
        // (which would say false).
        var leftIsNum = double.TryParse(left, NumberStyles.Any, CultureInfo.InvariantCulture, out var ln);
        var rightIsNum = double.TryParse(right, NumberStyles.Any, CultureInfo.InvariantCulture, out var rn);

        if (leftIsNum && rightIsNum)
        {
            return op switch
            {
                "==" => Math.Abs(ln - rn) < double.Epsilon,
                "!=" => Math.Abs(ln - rn) >= double.Epsilon,
                ">=" => ln >= rn,
                "<=" => ln <= rn,
                ">" => ln > rn,
                "<" => ln < rn,
                _ => false,
            };
        }

        var cmp = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return op switch
        {
            "==" => cmp == 0,
            "!=" => cmp != 0,
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            ">" => cmp > 0,
            "<" => cmp < 0,
            _ => false,
        };
    }

    private static bool Truthy(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return false;
        if (bool.TryParse(v, out var b)) return b;
        if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
            return Math.Abs(n) > double.Epsilon;
        // "null", the unresolved sentinel, and empty arrays/objects are falsy;
        // any other non-empty string is truthy.
        return v is not ("null" or Unresolved or "[]" or "{}");
    }
}
