using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Git;
using flow_weaver_backend.Services.Identity;
using CredentialModel = flow_weaver_backend.Models.Credential;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

// Creates a brand-new GitHub repository via the GitHub REST API and
// (by default) auto-registers it as a FlowWeaver GitRepository so the
// follow-up git_write_file / git_read_file / git_pull tools can work
// against it without an extra round-trip through the user.
//
// Why a dedicated tool instead of using execute_operation? The
// dynamic-spec executor needs an Integration row + an OpenAPI spec for
// GitHub. Most deployments don't have those, and we want the natural
// "create + push a file" flow to be one fast path. SSH-key creation
// flows (deploy keys) and other GitHub operations stay outside scope —
// this is intentionally narrow.
//
// Provider scope: GitHub only in v1. GitLab uses /projects with a
// different shape; we add a separate tool when the user asks.
public sealed class GitCreateRemoteRepositoryHandler : IToolHandler
{
    public string Name => "git_create_remote_repository";

    public string Description =>
        "Create a NEW repository on GitHub using the PAT from a registered " +
        "credential, then auto-register it inside FlowWeaver. Use this when " +
        "the user says 'create a repo' / 'create a github repository' — the " +
        "other git_* tools (read_file, write_file, pull, push) only work " +
        "against repos that already exist remotely. By default the repo is " +
        "private, owned by the PAT's user, with auto_init=true so it has a " +
        "main branch from the first commit. Returns the new GitHub URLs AND " +
        "the FlowWeaver repository_id you pass to git_write_file next. " +
        "single_confirm tier — propose ONE plan, get user's yes, execute.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["name", "auth_credential_id"],
          "properties": {
            "name": {
              "type": "string", "minLength": 1,
              "description": "Repository name (without owner). GitHub will reject names with spaces or special chars."
            },
            "auth_credential_id": {
              "type": "string", "format": "uuid",
              "description": "Credential UUID. Type should be 'git_token' (PAT in password field). The PAT must include the 'repo' scope."
            },
            "owner": {
              "type": "string",
              "description": "Org or user name to own the repo. Omit to create under the PAT's authenticated user. Pass an org name to create inside that org (PAT needs org permissions)."
            },
            "description": { "type": "string" },
            "private":     { "type": "boolean", "description": "Defaults to true." },
            "auto_init":   { "type": "boolean", "description": "Defaults to true. Creates an initial commit + README so the repo has a default branch immediately." },
            "default_branch": {
              "type": "string",
              "description": "Initial branch name. Defaults to 'main'. Only honored when auto_init=true."
            },
            "register_in_flowweaver": {
              "type": "boolean",
              "description": "Defaults to true. After creating on GitHub, register the repo in FlowWeaver linked to the same credential, so git_write_file / git_pull / etc. can operate on it."
            },
            "register_name": {
              "type": "string",
              "description": "Optional FlowWeaver display name for the registration. Defaults to the GitHub repo name."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IRepository<CredentialModel> _credentials;
    private readonly ICurrentUser _caller;
    private readonly ICredentialEncryptionService _crypto;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IGitService _git;
    private readonly ILogger<GitCreateRemoteRepositoryHandler> _logger;

    public GitCreateRemoteRepositoryHandler(
        IRepository<CredentialModel> credentials,
        ICurrentUser caller,
        ICredentialEncryptionService crypto,
        IHttpClientFactory httpFactory,
        IGitService git,
        ILogger<GitCreateRemoteRepositoryHandler> logger)
    {
        _credentials = credentials;
        _caller = caller;
        _crypto = crypto;
        _httpFactory = httpFactory;
        _git = git;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = GetString(args, "name")?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return Err("name is required");
        var credIdStr = GetString(args, "auth_credential_id");
        if (string.IsNullOrWhiteSpace(credIdStr) || !Guid.TryParse(credIdStr, out var credId))
            return Err("auth_credential_id is required and must be a UUID");

        // Resolve credential + decrypt PAT.
        var cred = await _credentials.GetByIdAsync(credId, activeOnly: true, tracking: false, ct);
        if (cred is null) return Err("auth_credential_id not found");
        var pat = _crypto.Decrypt(cred.EncryptedPassword);
        if (string.IsNullOrEmpty(pat))
            return Err("credential has no token in the password field — store the GitHub PAT there");

        var owner = GetString(args, "owner")?.Trim();
        var description = GetString(args, "description");
        var isPrivate = GetBool(args, "private", fallback: true);
        var autoInit = GetBool(args, "auto_init", fallback: true);
        var defaultBranch = (GetString(args, "default_branch") ?? "main").Trim();
        var registerInFw = GetBool(args, "register_in_flowweaver", fallback: true);
        var registerName = GetString(args, "register_name")?.Trim();

        var endpoint = string.IsNullOrEmpty(owner)
            ? "https://api.github.com/user/repos"
            : $"https://api.github.com/orgs/{Uri.EscapeDataString(owner!)}/repos";

        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["private"] = isPrivate,
            ["auto_init"] = autoInit,
        };
        if (!string.IsNullOrEmpty(description)) body["description"] = description;
        if (autoInit && !string.IsNullOrEmpty(defaultBranch) && defaultBranch != "main")
            body["default_branch"] = defaultBranch;

        using var http = _httpFactory.CreateClient("rest_call");
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        // Pin HTTP/1.1: .NET's HTTP/2 path against api.github.com has been
        // observed to hang ~20s and surface as `HttpIOException: ResponseEnded`
        // on this POST. wget/curl over HTTP/1.1 respond in <1s, so force it.
        req.Version = System.Net.HttpVersion.Version11;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("FlowWeaver", "1.0"));
        req.Content = JsonContent.Create(body);

        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ai.tool.git_create_remote.network_error endpoint={Endpoint}", endpoint);
            return Err($"GitHub API call failed: {ex.Message}");
        }
        // Always release the underlying socket back to the pool, even
        // when the body parse path below throws.
        using var _resScope = res;

        var raw = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "ai.tool.git_create_remote.api_error status={Status} endpoint={Endpoint} body_len={BodyLen}",
                (int)res.StatusCode, endpoint, raw.Length);
            // GitHub bodies are JSON with `message` + optional `errors[]`.
            // Surface them verbatim so the agent can pass them to the user.
            return Err($"GitHub returned {(int)res.StatusCode}: {Truncate(raw, 600)}");
        }

        // Parse the bits we need from the response.
        JsonDocument repo;
        try { repo = JsonDocument.Parse(raw); }
        catch (JsonException ex)
        {
            return Err($"GitHub response was not JSON: {ex.Message}");
        }
        using (repo)
        {
            var root = repo.RootElement;
            var fullName = root.TryGetProperty("full_name", out var fn) ? fn.GetString() ?? string.Empty : string.Empty;
            var htmlUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? string.Empty : string.Empty;
            var cloneUrl = root.TryGetProperty("clone_url", out var cu) ? cu.GetString() ?? string.Empty : string.Empty;
            var sshUrl = root.TryGetProperty("ssh_url", out var su) ? su.GetString() ?? string.Empty : string.Empty;
            var apiBranch = root.TryGetProperty("default_branch", out var db) ? db.GetString() ?? defaultBranch : defaultBranch;

            _logger.LogInformation(
                "ai.tool.git_create_remote.created full_name={FullName} private={Private}",
                fullName, isPrivate);

            Guid? fwRepoId = null;
            string? fwRegisterError = null;
            if (registerInFw && !string.IsNullOrEmpty(cloneUrl))
            {
                var dto = new CreateGitRepository
                {
                    Name = string.IsNullOrEmpty(registerName) ? name : registerName!,
                    Url = cloneUrl,
                    DefaultBranch = apiBranch,
                    AuthCredentialId = credId,
                    Description = description,
                };
                var (ok, value, err) = GitToolHelpers.Unwrap(await _git.CreateAsync(dto, ct));
                if (ok && value is not null)
                {
                    fwRepoId = value.GitRepositoryId;
                }
                else
                {
                    // Don't fail the whole call — the GitHub repo IS created.
                    // Surface the registration failure so the user can register
                    // manually if they want.
                    fwRegisterError = err.ValueKind == JsonValueKind.Object
                        ? err.GetRawText()
                        : "registration in FlowWeaver failed";
                    _logger.LogWarning(
                        "ai.tool.git_create_remote.register_failed full_name={FullName} reason={Reason}",
                        fullName, fwRegisterError);
                }
            }

            return JsonSerializer.SerializeToElement(new
            {
                ok = true,
                full_name = fullName,
                html_url = htmlUrl,
                clone_url = cloneUrl,
                ssh_url = sshUrl,
                @private = isPrivate,
                default_branch = apiBranch,
                // FlowWeaver-side state. When registered, the agent should
                // pass repository_id to git_write_file in the next turn.
                registered_in_flowweaver = fwRepoId is not null,
                repository_id = fwRepoId,
                register_error = fwRegisterError,
            });
        }
    }

    private static JsonElement Err(string msg) =>
        JsonSerializer.SerializeToElement(new { error = msg });

    private static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool GetBool(JsonElement obj, string key, bool fallback) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}
