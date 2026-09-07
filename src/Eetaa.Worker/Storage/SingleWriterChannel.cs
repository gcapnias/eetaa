using System.Threading.Channels;

namespace Eetaa.Worker.Storage;

public interface ISingleWriterChannel
{
    ChannelWriter<SettlementPayload> Writer { get; }
    ChannelReader<SettlementPayload> Reader { get; }
    ValueTask EnqueueAsync(SettlementPayload payload, CancellationToken cancellationToken = default);
    void Complete(Exception? error = null);
}

public sealed class SingleWriterChannel : ISingleWriterChannel
{
    private readonly Channel<SettlementPayload> _channel;

    public SingleWriterChannel(int capacity = 1000)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        };
        _channel = Channel.CreateBounded<SettlementPayload>(options);
    }

    public ChannelWriter<SettlementPayload> Writer => _channel.Writer;
    public ChannelReader<SettlementPayload> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(SettlementPayload payload, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(payload, cancellationToken);
    }

    public void Complete(Exception? error = null)
    {
        _channel.Writer.TryComplete(error);
    }
}
