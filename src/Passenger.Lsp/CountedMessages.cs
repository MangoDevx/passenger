using System.Runtime.CompilerServices;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Passenger.Lsp;

/// <summary>The connection's header-delimited messages, each of the server's words that its colours moved noted with how
/// many messages had crossed to the server whole as its last byte was read off the connection, which its parsing follows
/// later: the client handles what the server sends one message at a time, in order, so a word handled after a request
/// went out may have arrived before it, and a request waits for its writing and then for the copy to the server's input
/// after its caller sends it (<see cref="SentRequest"/>).</summary>
internal sealed class CountedMessages : HeaderDelimitedMessageHandler
{
    private readonly TracedStream output;
    private readonly ConditionalWeakTable<object, SentRequest> followed = new();
    private long written;
    private long lastWordAfter = -1;

    public CountedMessages(TracedStream input, TracedStream output, IJsonRpcMessageFormatter formatter)
        : base(input, output, formatter)
    {
        this.output = output;
    }

    /// <summary>The messages that had crossed to the server whole when its last word that its colours moved was read; -1
    /// before one is.</summary>
    public long LastWordAfter => Interlocked.Read(ref lastWordAfter);

    /// <summary>Has the request of these parameters (by reference) tell <paramref name="request"/> its place among the
    /// messages as it is written.</summary>
    public void Follow(object parameters, SentRequest request) => followed.AddOrUpdate(parameters, request);

    // Pooled: every message the server sends passes here, most finishing later than they began.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        JsonRpcMessage? message = await base.ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        if (message is not null)
        {
            long crossed = output.CrossedAcrossBeforeNext();
            if (message is JsonRpcRequest { Method: LspMethods.SemanticTokensRefresh })
            {
                Interlocked.Exchange(ref lastWordAfter, crossed);
            }
        }
        return message;
    }

    // One message a frame, in order, under the handler's own lock: the frames cross the server's input in this order.
    protected override void Write(JsonRpcMessage content, CancellationToken cancellationToken)
    {
        base.Write(content, cancellationToken);
        long frame = Interlocked.Increment(ref written);
        if (content is JsonRpcRequest { Arguments: { } parameters } && followed.TryGetValue(parameters, out SentRequest? request))
        {
            request.Written(this, frame);
        }
    }
}

/// <summary>A colour request on its way to the server, placed among the server's words that its colours moved: one the
/// server could read only after it had sent every word read so far answers with what they told of.</summary>
public sealed class SentRequest
{
    private CountedMessages? messages;
    private long frame;

    /// <summary>Whether every word read so far was read before the request had crossed to the server whole, as one still
    /// waiting its turn to be written is.</summary>
    public bool AfterEveryWord
    {
        get
        {
            long at = Volatile.Read(ref frame);
            return at == 0 || messages!.LastWordAfter < at;
        }
    }

    internal void Written(CountedMessages by, long at)
    {
        messages = by;
        Volatile.Write(ref frame, at);
    }
}
