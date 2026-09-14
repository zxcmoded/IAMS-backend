namespace IAMS.Api.Common.Time;

/// <summary>Abstraction over the system clock so time-dependent logic (token/OTP expiry) is testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
