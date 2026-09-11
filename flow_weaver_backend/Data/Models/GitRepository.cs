namespace flow_weaver_backend.Models;

// Registration of a remote Git repository the platform is allowed to
// operate on. Auth is delegated to the existing Credential
// model: AuthCredentialId points at a row whose Type="git_token" with
// a username (typically a PAT scope label) + EncryptedPassword (the
// PAT itself). SSH-key auth is not supported in v1 — the model has
// space for it, but GitService only wires HTTPS+token today.
//
// LocalPath is computed deterministically from RepoId and
// stored so an admin can inspect it from the UI; the on-disk checkout
// lives under FlowWeaverConfig:GitRoot.
public class GitRepository : BaseModel
{
    public Guid GitRepositoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public Guid? AuthCredentialId { get; set; }
    public string? LocalPath { get; set; }
    public DateTime? LastFetchedAt { get; set; }
    public string? Description { get; set; }
}
