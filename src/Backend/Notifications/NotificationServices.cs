namespace ChasePie.Lookout.Backend.Notifications;

public static class NotificationServices
{
    /// <summary>A sender for each kind of Notification channel, and the dispatcher that picks between them.</summary>
    public static void AddNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        var pushoverBaseAddress = HttpUrls.ConfiguredBaseAddress(configuration, "Notifications:PushoverBaseUrl",
            PushoverSender.DefaultBaseUrl);

        // A sender's request URL or body holds its Notification channel's secrets, so its requests are never logged. A
        // notification is sent while its Run holds a slot, so a Notification channel's service that doesn't answer is given
        // up on soon.
        var sendTimeout = TimeSpan.FromSeconds(10);
        services.AddHttpClient(nameof(DiscordSender), client => client.Timeout = sendTimeout).RemoveAllLoggers();
        services.AddHttpClient(nameof(PushoverSender), client =>
        {
            client.BaseAddress = pushoverBaseAddress;
            client.Timeout = sendTimeout;
        }).RemoveAllLoggers();
        services.AddTransient<INotificationChannelSender>(provider =>
            new DiscordSender(provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DiscordSender))));
        services.AddTransient<INotificationChannelSender>(provider =>
            new PushoverSender(provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PushoverSender))));
        services.AddScoped<NotificationDispatcher>();
    }
}
