namespace flow_weaver_backend.Services.Messaging;

// Resolves the IMessagingProvider for a channel's Provider string. Every
// concrete provider is registered as IMessagingProvider; this indexes them by
// name. Returns null for an unknown/unregistered provider so callers can reject
// cleanly (e.g. a channel row for a provider not yet shipped).
public interface IMessagingProviderResolver
{
    IMessagingProvider? Resolve(string provider);
}

public sealed class MessagingProviderResolver : IMessagingProviderResolver
{
    private readonly Dictionary<string, IMessagingProvider> _byName;

    public MessagingProviderResolver(IEnumerable<IMessagingProvider> providers)
    {
        _byName = providers.ToDictionary(p => p.Provider, StringComparer.OrdinalIgnoreCase);
    }

    public IMessagingProvider? Resolve(string provider) =>
        _byName.TryGetValue(provider, out var p) ? p : null;
}
