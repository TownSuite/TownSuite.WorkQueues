using System.Diagnostics;

namespace TownSuite.WorkQueues;

/// <summary>
/// <see cref="System.Diagnostics.Activity"/> spans emitted by every message bus transport.
/// Subscribe with OpenTelemetry via <c>.AddSource(WorkQueueTracing.ActivitySourceName)</c>.
/// </summary>
/// <remarks>
/// <para>Publishing starts a <see cref="ActivityKind.Producer"/> span and stores its W3C trace
/// context with the message. Delivering the message starts a <see cref="ActivityKind.Consumer"/>
/// span whose parent is that stored context, and makes it <see cref="Activity.Current"/> while
/// consumers run, so a request and the work it queued appear as one trace.</para>
/// <para>When nothing listens to the source no span is created, but the caller's
/// <see cref="Activity.Current"/> is still stored, so the trace is joined up as soon as a
/// listener is added on the consuming side.</para>
/// </remarks>
public static class WorkQueueTracing
{
    /// <summary>The activity source name to subscribe to.</summary>
    public const string ActivitySourceName = "TownSuite.WorkQueues";

    private static readonly ActivitySource Source = new(ActivitySourceName,
        typeof(WorkQueueTracing).Assembly.GetName().Version?.ToString());

    internal static Activity? StartPublish(string transport, string channel, int messageCount)
    {
        var activity = Source.StartActivity($"publish {channel}", ActivityKind.Producer);
        if (activity is { IsAllDataRequested: true })
        {
            SetCommonTags(activity, transport, channel);
            activity.SetTag("messaging.operation.type", "send");
            activity.SetTag("messaging.operation.name", "publish");
            if (messageCount != 1) activity.SetTag("messaging.batch.message_count", messageCount);
        }
        return activity;
    }

    internal static Activity? StartProcess(string transport, MessageDto message)
    {
        var (traceParent, traceState) = SplitContext(message.TraceContext);
        ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parent);
        var activity = Source.StartActivity($"process {message.Channel}", ActivityKind.Consumer, parent);
        if (activity is { IsAllDataRequested: true })
        {
            SetCommonTags(activity, transport, message.Channel);
            activity.SetTag("messaging.operation.type", "process");
            activity.SetTag("messaging.operation.name", "process");
            activity.SetTag("messaging.message.id", message.MessageId.ToString());
            activity.SetTag("townsuite.workqueues.delivery_attempt", message.RetryCount + 1);
        }
        return activity;
    }

    /// <summary>
    /// The trace context to store with a published message: the publish span's, or the caller's
    /// when no span was created. Returned as one string, the W3C <c>traceparent</c> optionally
    /// followed by a space and the <c>tracestate</c>.
    /// </summary>
    internal static string? CaptureContext(Activity? publishActivity)
    {
        var activity = publishActivity ?? Activity.Current;
        if (activity == null || activity.IdFormat != ActivityIdFormat.W3C || activity.Id == null) return null;
        return string.IsNullOrEmpty(activity.TraceStateString) ? activity.Id : $"{activity.Id} {activity.TraceStateString}";
    }

    private static (string? TraceParent, string? TraceState) SplitContext(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return (null, null);
        var space = stored.IndexOf(' ');
        return space < 0 ? (stored, null) : (stored[..space], stored[(space + 1)..]);
    }

    internal static void SetOutcome(Activity? activity, string outcome) =>
        activity?.SetTag("townsuite.workqueues.outcome", outcome);

    internal static void RecordException(Activity? activity, Exception ex)
    {
        if (activity == null) return;
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("error.type", ex.GetType().FullName);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"]       = ex.GetType().FullName,
            ["exception.message"]    = ex.Message,
            ["exception.stacktrace"] = ex.ToString()
        }));
    }

    private static void SetCommonTags(Activity activity, string transport, string channel)
    {
        activity.SetTag("messaging.system", transport);
        activity.SetTag("messaging.destination.name", channel);
    }
}
