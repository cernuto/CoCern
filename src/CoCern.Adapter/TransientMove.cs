namespace CoCern.Adapter;

/// <summary>
/// Retries a rename that Windows refuses for a moment. A virus scanner, the search indexer or a reader without delete sharing can hold a just-written file or folder
/// briefly, and the rename then fails with access denied or a sharing violation although nothing is wrong. Seen once each in AdapterDeployment.Publish and
/// NodeState.WritePrivately under heavy parallel load; the cause was not established, so this only rides out a short hold. Every other failure, and one that outlasts
/// the attempts, still surfaces.
/// </summary>
internal static class TransientMove
{
    private const int Attempts = 5;
    private const int SharingViolation = unchecked((int)0x80070020);

    internal static void Run(Action move, Action<TimeSpan>? wait = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move();
                return;
            }
            catch (Exception exception) when (attempt < Attempts && OperatingSystem.IsWindows() && (exception is UnauthorizedAccessException or IOException { HResult: SharingViolation }))
            {
                (wait ?? Thread.Sleep)(TimeSpan.FromMilliseconds(50 * (1 << (attempt - 1))));
            }
        }
    }
}
