using ChasePie.Lookout.Backend.Auth;
using Microsoft.AspNetCore.Http;

namespace Backend.UnitTests;

[TestClass]
public sealed class CsrfProtectionTests
{
    private static HttpRequest Request(
        string method,
        bool customHeader = true,
        string? fetchSite = null,
        string? origin = null,
        string? upgrade = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("lookout.example.ts.net");
        if (customHeader)
        {
            context.Request.Headers[CsrfProtection.HeaderName] = "1";
        }

        if (fetchSite is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        }

        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (upgrade is not null)
        {
            context.Request.Headers.Upgrade = upgrade;
        }

        return context.Request;
    }

    [TestMethod]
    [DataRow("GET")]
    [DataRow("HEAD")]
    [DataRow("OPTIONS")]
    [DataRow("TRACE")]
    public void SafeMethodsAreAlwaysAllowed(string method)
    {
        // Even from another site and without the header: they don't change data
        Assert.IsNull(CsrfProtection.GetRejection(Request(method, customHeader: false, fetchSite: "cross-site")));
    }

    [TestMethod]
    [DataRow("POST")]
    [DataRow("PUT")]
    [DataRow("PATCH")]
    [DataRow("DELETE")]
    public void ARequestFromLookoutItselfIsAllowed(string method)
    {
        Assert.IsNull(CsrfProtection.GetRejection(Request(method, fetchSite: "same-origin")));
    }

    [TestMethod]
    public void TheCustomHeaderIsRequired()
    {
        var rejection = CsrfProtection.GetRejection(Request("POST", customHeader: false, fetchSite: "same-origin"));

        Assert.IsNotNull(rejection);
        Assert.Contains(CsrfProtection.HeaderName, rejection);
    }

    [TestMethod]
    [DataRow("same-site")]
    [DataRow("cross-site")]
    [DataRow("none")]
    public void OtherFetchSitesAreRejected(string fetchSite)
    {
        Assert.IsNotNull(CsrfProtection.GetRejection(Request("POST", fetchSite: fetchSite)));
    }

    [TestMethod]
    public void FetchMetadataWinsOverAMatchingOrigin()
    {
        var request = Request("POST", fetchSite: "cross-site", origin: "https://lookout.example.ts.net");

        Assert.IsNotNull(CsrfProtection.GetRejection(request));
    }

    [TestMethod]
    [DataRow("https://lookout.example.ts.net")]
    [DataRow("HTTPS://LOOKOUT.EXAMPLE.TS.NET")]
    public void WithoutFetchMetadataTheOwnOriginIsAllowed(string origin)
    {
        Assert.IsNull(CsrfProtection.GetRejection(Request("POST", origin: origin)));
    }

    [TestMethod]
    [DataRow("https://grafana.example.ts.net")]
    [DataRow("http://lookout.example.ts.net")]
    [DataRow("https://lookout.example.ts.net:8443")]
    [DataRow("null")]
    public void WithoutFetchMetadataAnyOtherOriginIsRejected(string origin)
    {
        Assert.IsNotNull(CsrfProtection.GetRejection(Request("POST", origin: origin)));
    }

    [TestMethod]
    public void ARequestWithNeitherFetchMetadataNorOriginIsRejected()
    {
        var rejection = CsrfProtection.GetRejection(Request("POST"));

        Assert.IsNotNull(rejection);
        Assert.Contains("Sec-Fetch-Site", rejection);
    }

    [TestMethod]
    public void AWebSocketFromLookoutItselfIsAllowedWithoutTheCustomHeader()
    {
        // Browsers can't add headers to a WebSocket handshake
        Assert.IsNull(CsrfProtection.GetRejection(
            Request("GET", customHeader: false, fetchSite: "same-origin", upgrade: "websocket")));
        Assert.IsNull(CsrfProtection.GetRejection(
            Request("CONNECT", customHeader: false, origin: "https://lookout.example.ts.net")));
    }

    [TestMethod]
    [DataRow("GET", "websocket")]
    [DataRow("CONNECT", null)]
    public void AWebSocketFromAnotherOriginIsRejected(string method, string? upgrade)
    {
        // WebSockets aren't covered by CORS, and a sibling subdomain is sent the SameSite=Strict cookie
        Assert.IsNotNull(CsrfProtection.GetRejection(
            Request(method, fetchSite: "same-site", upgrade: upgrade)));
        Assert.IsNotNull(CsrfProtection.GetRejection(
            Request(method, origin: "https://grafana.example.ts.net", upgrade: upgrade)));
        Assert.IsNotNull(CsrfProtection.GetRejection(Request(method, upgrade: upgrade)));
    }
}
