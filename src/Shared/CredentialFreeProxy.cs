using System.Net;

namespace MessageBus.Http;

// Shared source: anonymous transports retain system/PAC bypass and routing decisions,
// but never consult or mutate the underlying proxy's credentials (including OS credentials).
internal sealed class CredentialFreeProxy(IWebProxy routing) : IWebProxy
{
    internal static IWebProxy Wrap(IWebProxy? proxy, Func<IWebProxy>? defaultProxy = null) =>
        new CredentialFreeProxy(proxy ?? (defaultProxy ?? (() => HttpClient.DefaultProxy))());

    public ICredentials? Credentials { get => null; set { } }

    public bool IsBypassed(Uri host) => routing.IsBypassed(host);

    public Uri? GetProxy(Uri destination)
    {
        var proxy = routing.GetProxy(destination);
        // A custom proxy can return credentials in its URI as well as its Credentials property.
        return proxy is { UserInfo.Length: > 0 }
            ? new UriBuilder(proxy) { UserName = "", Password = "" }.Uri
            : proxy;
    }
}
