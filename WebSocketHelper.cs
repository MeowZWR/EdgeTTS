using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace EdgeTTS;

internal static class WebSocketHelper
{
    private const int BUFFER_SIZE = 5 * 1024;

    public static async Task SendTextAsync(
        this WebSocket ws,
        string message,
        CancellationToken cancellationToken)
    {
        var buffer = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(
            new ReadOnlyMemory<byte>(buffer),
            WebSocketMessageType.Text,
            true,
            cancellationToken).ConfigureAwait(false);
    }

    public sealed record WebSocketMessage
    {
        public static readonly WebSocketMessage Close = new(WebSocketMessageType.Close);

        public WebSocketMessageType Type          { get; }
        public string?              MessageStr    { get; }
        public byte[]?              MessageBinary { get; }

        public WebSocketMessage(string message)
            : this(WebSocketMessageType.Text, message) { }

        public WebSocketMessage(byte[] message)
            : this(WebSocketMessageType.Binary, null, message) { }

        private WebSocketMessage(WebSocketMessageType type, string? messageStr = null, byte[]? messageBinary = null)
        {
            Type = type;
            MessageStr = messageStr;
            MessageBinary = messageBinary;
        }

        public override string ToString()
        {
            return
                $"{nameof(Type)}: {Type}, {nameof(MessageStr)}: {MessageStr}, {nameof(MessageBinary)}: byte[{MessageBinary?.Length ?? -1}]";
        }
    }

    public sealed class WebSocketSession : IDisposable
    {
        private readonly WebSocket _webSocket;
        private readonly StringBuilder _textBuffer;
        private readonly MemoryStream _binaryBuffer;
        private readonly byte[] _receiveBuffer;
        private bool _disposed;

        public WebSocketSession(WebSocket webSocket)
        {
            _webSocket = webSocket;
            _textBuffer = new StringBuilder();
            _binaryBuffer = new MemoryStream();
            _receiveBuffer = new byte[BUFFER_SIZE];
        }

        public async Task<WebSocketMessage> ReceiveNextMessageAsync(CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WebSocketSession));

            ResetBuffers();
            WebSocketMessageType? previousMessageType = null;

            try
            {
                while (true)
                {
                    var result = await _webSocket.ReceiveAsync(
                                     new ArraySegment<byte>(_receiveBuffer),
                                     cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Debug.Assert(_textBuffer.Length == 0);
                        Debug.Assert(_binaryBuffer.Length == 0);
                        return WebSocketMessage.Close;
                    }

                    await ProcessWebSocketMessageAsync(result, previousMessageType).ConfigureAwait(false);
                    previousMessageType = result.MessageType;

                    if (result.EndOfMessage) return CreateMessage(result.MessageType);
                }
            }
            catch (Exception ex) { throw new IOException("Failed to receive WebSocket message", ex); }
        }

        private async Task ProcessWebSocketMessageAsync(
            WebSocketReceiveResult result,
            WebSocketMessageType? previousMessageType)
        {
            if (previousMessageType.HasValue && previousMessageType != result.MessageType)
                throw new IOException(
                    $"Unexpected message type change from {previousMessageType} to {result.MessageType}");

            if (result.Count <= 0) return;

            switch (result.MessageType)
            {
                case WebSocketMessageType.Text:
                    _textBuffer.Append(Encoding.UTF8.GetString(_receiveBuffer, 0, result.Count));
                    break;

                case WebSocketMessageType.Binary:
                    await _binaryBuffer.WriteAsync(_receiveBuffer.AsMemory(0, result.Count)).ConfigureAwait(false);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(result.MessageType),
                        result.MessageType,
                        "Unsupported WebSocket message type");
            }
        }

        private void ResetBuffers()
        {
            _textBuffer.Clear();
            _binaryBuffer.Position = 0;
            _binaryBuffer.SetLength(0);
        }

        private WebSocketMessage CreateMessage(WebSocketMessageType messageType)
        {
            return messageType switch
            {
                WebSocketMessageType.Text => new WebSocketMessage(_textBuffer.ToString()),
                WebSocketMessageType.Binary => new WebSocketMessage(_binaryBuffer.ToArray()),
                _ => throw new ArgumentOutOfRangeException(nameof(messageType))
            };
        }

        public void Dispose()
        {
            if (_disposed) return;

            _binaryBuffer.Dispose();
            _disposed = true;
        }
    }

    public static Task<bool> IsConnectedAsync(this WebSocket webSocket) => Task.FromResult(webSocket.State == WebSocketState.Open);

    public static async Task SafeCloseAsync(
        this WebSocket webSocket,
        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure,
        string? statusDescription = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (await webSocket.IsConnectedAsync().ConfigureAwait(false))
                await webSocket.CloseAsync(closeStatus, statusDescription ?? "Closing", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            webSocket.Abort();
        }
    }
}
