using System.Diagnostics;
using GpuSuite.Core.Diagnostics;

namespace GpuSuite.Measurement.Monitoring;

internal static class ChannelFreshness
{
    private const double FreshWithinMs = 2000;

    internal static MonitoringChannel[] Evaluate(IEnumerable<MonitoringChannel> channels, bool online)
        => channels.Select(channel =>
        {
            double? age = channel.ReceivedTicks != 0
                ? Stopwatch.GetElapsedTime(channel.ReceivedTicks).TotalMilliseconds : null;
            return channel with
            {
                SourceAgeMs = age,
                Fresh = online && channel.Valid && channel.Status == "sampling" && age is <= FreshWithinMs
            };
        }).ToArray();
}
