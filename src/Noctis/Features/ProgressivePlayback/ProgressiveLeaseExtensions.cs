namespace Noctis.Features.ProgressivePlayback;

internal static class ProgressiveLeaseExtensions
{
    internal static ProgressiveMediaInput CreateInput(this ProgressiveTransfer.Lease lease) => new(lease.Buffer);
}
