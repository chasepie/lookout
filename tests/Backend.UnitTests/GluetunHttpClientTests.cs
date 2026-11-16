using System.Diagnostics;
using System.Net;
using System.Text;
using ChasePie.Lookout.Backend.VpnServices;

namespace ChasePie.Lookout.Backend.UnitTests;

/// <summary>
/// How long <see cref="GluetunHttpClient"/> waits for gluetun: reads, which the run worker's loop waits for, time out
/// sooner than switches.
/// </summary>
[TestClass]
public sealed class GluetunHttpClientTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AReadThatGluetunDoesntAnswerTimesOutAfterFiveSeconds()
    {
        var client = CreateClient(answerAfter: Timeout.InfiniteTimeSpan);
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsExactlyAsync<GluetunException>(
            () => client.GetVpnSettingsAsync(TestContext.CancellationToken));

        Assert.AreEqual("gluetun didn't answer within 5 seconds.", ex.Message);
        Assert.IsLessThan(TimeSpan.FromSeconds(10), stopwatch.Elapsed);
    }

    [TestMethod]
    public async Task ASwitchWaitsLongerThanARead()
    {
        // gluetun answers a switch once it has started reconnecting, which can take a while
        var client = CreateClient(answerAfter: GluetunHttpClient.ReadTimeout + TimeSpan.FromSeconds(1));

        await client.SetRegionAsync("Belgium", TestContext.CancellationToken);
    }

    private static GluetunHttpClient CreateClient(TimeSpan answerAfter) => new(
        new HttpClient(new SlowHandler(answerAfter)) { BaseAddress = new Uri("http://gluetun.test/") });

    private sealed class SlowHandler(TimeSpan answerAfter) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(answerAfter, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
