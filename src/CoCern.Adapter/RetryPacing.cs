namespace CoCern.Adapter;

/// <summary>How adapter retries are spread out, so many adapters that lost the hub together do not all come back at the same instant.</summary>
internal static class RetryPacing
{
    /// <summary>Uniform in [0.8, 1.2): the same ±20% LocalChatWake.HeldRetry uses.</summary>
    internal static double Factor() => 0.8 + 0.4 * Random.Shared.NextDouble();

    internal static TimeSpan Jitter(TimeSpan delay) => delay * Factor();

    /// <summary>15 s, 30 s, then 60 s for 1, 2, and 3 or more reads in a row that did not reach the hub.</summary>
    internal static TimeSpan Unreachable(int misses) => misses switch
    {
        <= 1 => TimeSpan.FromSeconds(15),
        2 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromSeconds(60),
    };
}
