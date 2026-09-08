using System.Buffers;
using System.Threading.Channels;

namespace KetPsi.Extensions.Logging.Console.Internals;

public sealed class ConsoleLogProcessor : IDisposable
{
    private readonly Channel<LogPayload> _messageQueue;
    private readonly Task _processTask;
    private readonly Stream _stdout;
    private readonly Stream _stderr;
    private readonly CancellationTokenSource _cts = new();


    public ConsoleLogProcessor(Stream? stdout = null, Stream? stderr = null)
    {
        // Unbounded channel ensures the main thread NEVER blocks on logging.
        // We can use CreateBounded if you prefer to drop logs under extreme backpressure.
        _messageQueue = Channel.CreateUnbounded<LogPayload>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _stdout = stdout ?? System.Console.OpenStandardOutput();
        _stderr = stderr ?? System.Console.OpenStandardError();
        _processTask = Task.Factory.StartNew(ProcessQueueAsync, TaskCreationOptions.LongRunning);
    }

    public void Enqueue(ReadOnlySpan<byte> payload, bool isErrorStream)
    {
        // Rent an exact-fit array to transit the channel
        byte[] transitBuffer = ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(transitBuffer);

        var message = new LogPayload(transitBuffer, payload.Length, isErrorStream);

        if (!_messageQueue.Writer.TryWrite(message))
        {
            ArrayPool<byte>.Shared.Return(transitBuffer);
        }
    }

    private async Task ProcessQueueAsync()
    {
        var reader = _messageQueue.Reader;

        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                while (reader.TryRead(out LogPayload log))
                {
                    try
                    {
                        var stream = log.IsErrorStream ? _stderr : _stdout;
                        await stream.WriteAsync(log.RentedBuffer.AsMemory(0, log.Length), _cts.Token);
                    }
                    catch
                    {
                        // Suppress console write failures (e.g., container pipe closed)
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(log.RentedBuffer);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            //skip
        }
    }

    public void Dispose()
    {
        _messageQueue.Writer.TryComplete();
        _cts.Cancel();
        _processTask.Wait(TimeSpan.FromSeconds(2)); // Allow flush on shutdown

        _stdout.Dispose();
        _stderr.Dispose();
        _cts.Dispose();
    }
}