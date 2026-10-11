using System.Net;
using LoadTester.Core.Templates;

namespace LoadTester.Core.Engine;

/// <summary>One simulated user: its own connections, cookie jar and variables. Used by one iteration at a time.</summary>
/// <param name="createClient">Creates the user's connection pool; called again by <see cref="RenewClient"/>.</param>
internal sealed class VirtualUser(int id, Func<HttpMessageInvoker> createClient, CookieContainer? cookies) : ITemplateContext, IDisposable
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

    public int VirtualUserId { get; } = id;
    public long Iteration { get; private set; }
    public HttpMessageInvoker Client { get; private set; } = createClient();

    /// <summary>Starts a fresh session: variables are evaluated again and cookies are dropped.</summary>
    public void BeginIteration(CompiledScenario scenario)
    {
        Iteration++;
        _variables.Clear();
        foreach (var (name, value) in scenario.Variables)
            _variables[name] = value.Render(this);

        if (cookies is not null)
        {
            // The handler owns the container for its lifetime, so expire the cookies instead of replacing it.
            foreach (Cookie cookie in cookies.GetAllCookies())
                cookie.Expired = true;
        }
    }

    public void SetVariable(string name, string value) => _variables[name] = value;

    public bool TryGetVariable(string name, out string value) => _variables.TryGetValue(name, out value!);

    /// <summary>Closes the user's connections so the next request opens new ones. Cookies are kept.</summary>
    public void RenewClient()
    {
        Client.Dispose();
        Client = createClient();
    }

    public void Dispose() => Client.Dispose();
}
