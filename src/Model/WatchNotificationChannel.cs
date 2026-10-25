namespace ChasePie.Lookout.Model;

/// <summary>A Watch's choice of one Channel to notify.</summary>
public sealed class WatchChannel
{
    public Guid WatchId { get; set; }
    public Guid ChannelId { get; set; }
}
