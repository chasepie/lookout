using System.Net;
using ChasePie.Lookout.Backend.Auth;

namespace ChasePie.Lookout.Backend.UnitTests;

[TestClass]
public sealed class SignInRateLimitKeyTests
{
    [TestMethod]
    [DataRow("203.0.113.7", "203.0.113.7")]
    [DataRow("::ffff:203.0.113.7", "203.0.113.7")]
    [DataRow("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [DataRow("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [DataRow("::1", "::/64")]
    public void ClientsAreIpv4AddressesOrIpv6Networks(string address, string expected)
    {
        Assert.AreEqual(expected, OwnerAuthentication.GetRateLimitKey(IPAddress.Parse(address)));
    }

    [TestMethod]
    public void AnUnknownAddressIsOneClient()
    {
        Assert.AreEqual("unknown", OwnerAuthentication.GetRateLimitKey(null));
    }
}
