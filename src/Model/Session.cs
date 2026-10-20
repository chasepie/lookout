namespace ChasePie.Lookout.Model;

/// <summary>One login, from sign-in until logout. A session cookie is honoured only while its Session exists.</summary>
public sealed class Session
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int AccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
