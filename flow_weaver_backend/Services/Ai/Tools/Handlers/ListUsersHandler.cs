using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Identity;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Native list_users tool — lists active users. Admin
// only (matches UsersController). Read-only; used so the agent can resolve a
// username to a user_id before set_user_role / grant_resource_permission.
public sealed class ListUsersHandler : IToolHandler
{
    public string Name => "list_users";

    public string Description =>
        "Tier: autonomous. ADMIN ONLY. List active users "
        + "(user_id, username, email, role). Resolve a username to its user_id here before "
        + "calling set_user_role or grant_resource_permission.";

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        { "type": "object", "properties": {}, "additionalProperties": false }
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _caller;
    private readonly ILogger<ListUsersHandler> _logger;

    public ListUsersHandler(AppDbContext db, ICurrentUser caller, ILogger<ListUsersHandler> logger)
    {
        _db = db;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var users = await _db.Users
            .AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.Username)
            .Select(u => new { user_id = u.UserId, username = u.Username, email = u.Email, role = u.Role })
            .ToListAsync(ct);

        _logger.LogDebug("ai.tool.list_users.ok count={Count}", users.Count);
        return JsonSerializer.SerializeToElement(new { users });
    }
}
