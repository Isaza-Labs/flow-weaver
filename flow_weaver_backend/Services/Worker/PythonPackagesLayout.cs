namespace flow_weaver_backend.Services.Worker;

// Where admin-approved pip packages live inside the shared python_packages
// volume. The provisioner installs into this directory and the python_snippet
// sandbox bind-mounts it, so both sides must agree on the name.
public static class PythonPackagesLayout
{
    // Sub-directory of Python:PackagesDir holding every installed package.
    public const string SiteDirName = "site";

    public const string DefaultPackagesDir = "/app/pyenv";

    public static string ResolveSiteDir(string? configuredRoot)
    {
        var root = (configuredRoot ?? string.Empty).Trim();
        if (root.Length == 0) root = DefaultPackagesDir;
        return Path.Combine(root, SiteDirName);
    }
}
