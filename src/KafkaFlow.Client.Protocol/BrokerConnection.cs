namespace KafkaFlow.Client.Protocol
{
    using System;
    using System.Buffers.Binary;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using KafkaFlow.Client.Protocol.Messages;
    using KafkaFlow.Client.Protocol.Streams;

    public class BrokerConnection : IBrokerConnection
    {
        private readonly TcpClient client;
        private readonly NetworkStream stream;

        private readonly SortedDictionary<int, PendingRequest> pendingRequests =
            new SortedDictionary<int, PendingRequest>();

        private volatile int lastCorrelationId;

        private readonly CancellationTokenSource stopTokenSource = new CancellationTokenSource();
        private readonly Task listenerTask;

        private readonly byte[] messageSizeBuffer = new byte[sizeof(int)];

        private readonly string clientId;
        private readonly TimeSpan requestTimeout;

        public BrokerConnection(BrokerAddress address, string clientId, TimeSpan requestTimeout)
        {
            this.Address = address;
            this.clientId = clientId;
            this.requestTimeout = requestTimeout;
            this.client = new TcpClient(address.Host, address.Port);
            this.stream = this.client.GetStream();

            this.listenerTask = Task.Run(this.ListenStream);
        }

        public BrokerAddress Address { get; }

        private async Task ListenStream()
        {
            while (!this.stopTokenSource.IsCancellationRequested)
            {
                try
                {
                    var messageSize = await this.WaitForMessageSizeAsync().ConfigureAwait(false);
                    
                    Debug.WriteLine($"Received message with {messageSize}b size");

                    if (messageSize <= 0)
                        continue;

                    using var memory = new StaticMemoryStream(MemoryManager.Instance, messageSize);
                    memory.ReadFrom(this.stream);
                    memory.Position = 0;

                    using var tracked = new TrackedStream(memory, messageSize);
                    this.RespondMessage(tracked);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RespondMessage(TrackedStream source)
        {
            var correlationId = source.ReadInt32();

            if (!this.pendingRequests.TryGetValue(correlationId, out var request))
            {
                Debug.WriteLine($"Received Invalid message CID: {correlationId}");
                source.DiscardRemainingData();
                return;
            }

            Debug.WriteLine($"Received CID: {correlationId}, Type {request.ResponseType.Name} with {source.Size:N0}b");

            this.pendingRequests.Remove(correlationId);

            var response = (IResponse) Activator.CreateInstance(request.ResponseType);

            if (response is ITaggedFields)
                _ = source.ReadTaggedFields();

            response.Read(source);

            if (source.Size != source.Position)
                throw new Exception("Some data was not read from response");

            request.CompletionSource.TrySetResult(response);
        }

        private async Task<int> WaitForMessageSizeAsync()
        {
            await this.stream
                .ReadAsync(this.messageSizeBuffer, 0, sizeof(int), this.stopTokenSource.Token)
                .ConfigureAwait(false);

            return BinaryPrimitives.ReadInt32BigEndian(this.messageSizeBuffer);
        }

        public Task<TResponse> SendAsync<TResponse>(IRequestMessage<TResponse> request)
            where TResponse : class, IResponse
        {
            var pendingRequest = new PendingRequest(this.requestTimeout, request.ResponseType);

            lock (this.stream)
            {
                this.pendingRequests.TryAdd(
                    Interlocked.Increment(ref this.lastCorrelationId),
                    pendingRequest);

                this.stream.WriteMessage(
                    new Request(
                        this.lastCorrelationId,
                        this.clientId,
                        request));
            }

            return pendingRequest.GetTask<TResponse>();
        }

        public void Dispose()
        {
            this.stopTokenSource.Cancel();
            this.listenerTask.GetAwaiter().GetResult();
            this.listenerTask.Dispose();
            this.stream.Dispose();
            this.client.Dispose();
        }

        private readonly struct PendingRequest
        {
            public TimeSpan Timeout { get; }
            public Type ResponseType { get; }

            public readonly TaskCompletionSource<IResponse> CompletionSource;

            public PendingRequest(TimeSpan timeout, Type responseType)
            {
                this.Timeout = timeout;
                this.ResponseType = responseType;
                this.CompletionSource = new TaskCompletionSource<IResponse>();
            }

            public Task<TResponse> GetTask<TResponse>() where TResponse : IResponse =>
                this.CompletionSource.Task.ContinueWith(x => (TResponse) x.Result);
        }
    }
}