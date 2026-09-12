using System.Threading.Channels;

namespace Eetaa.Worker.Storage;

public interface ISingleWriterChannel<T>
{
    ChannelWriter<T> Writer { get; }
    ChannelReader<T> Reader { get; }
    int Count { get; }
    ValueTask EnqueueAsync(T payload, CancellationToken cancellationToken = default);
    void Complete(Exception? error = null);
}

public class SingleWriterChannel<T> : ISingleWriterChannel<T>
{
    private readonly Channel<T> _channel;

    public SingleWriterChannel(int capacity = 1000)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        };
        _channel = Channel.CreateBounded<T>(options);
    }

    public ChannelWriter<T> Writer => _channel.Writer;
    public ChannelReader<T> Reader => _channel.Reader;
    public int Count => _channel.Reader.Count;

    public ValueTask EnqueueAsync(T payload, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(payload, cancellationToken);
    }

    public void Complete(Exception? error = null)
    {
        _channel.Writer.TryComplete(error);
    }
}

public interface ISingleWriterChannel : ISingleWriterChannel<SettlementPayload>
{
}

public sealed class SingleWriterChannel : SingleWriterChannel<SettlementPayload>, ISingleWriterChannel
{
    public SingleWriterChannel(int capacity = 1000) : base(capacity)
    {
    }
}

public interface ILocalAuthorityChannel : ISingleWriterChannel<LocalAuthorityPayload>
{
}

public sealed class LocalAuthorityChannel : SingleWriterChannel<LocalAuthorityPayload>, ILocalAuthorityChannel
{
    public LocalAuthorityChannel(int capacity = 1000) : base(capacity)
    {
    }
}