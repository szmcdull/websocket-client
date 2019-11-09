using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Websocket.Client.Logging;

namespace Websocket.Client
{
    /// <summary>
    /// A simple websocket client with built-in reconnection and error handling
    /// </summary>
    public class WebsocketClient //: IWebsocketClient
    {
        public enum State
        {
            Disconnected,
            Connecting,
            Connected,
        }

        State state;
        Task _connectionTask;
        private CancellationTokenSource _cancelConnectionTask;

        private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();

        private Uri _uri;
        private readonly Func<ClientWebSocket> _clientFactory;

        //private DateTime _lastReceivedMsg = DateTime.UtcNow; 

        private bool _disposing;
        private ClientWebSocket _client;
        //private CancellationTokenSource _cancellation;
        //private CancellationTokenSource _cancellationTotal;

        private readonly Subject<ResponseMessage> _messageReceivedSubject = new Subject<ResponseMessage>();
        private readonly Subject<ReconnectionType> _reconnectionSubject = new Subject<ReconnectionType>();
        private readonly Subject<DisconnectionType> _disconnectedSubject = new Subject<DisconnectionType>();

        private readonly Channel<string> _messagesTextToSendQueue = Channel.CreateUnbounded<string>();
        private readonly Channel<byte[]> _messagesBinaryToSendQueue = Channel.CreateUnbounded<byte[]>();


        /// <summary>
        /// A simple websocket client with built-in reconnection and error handling
        /// </summary>
        /// <param name="url">Target websocket url (wss://)</param>
        /// <param name="clientFactory">Optional factory for native ClientWebSocket, use it whenever you need some custom features (proxy, settings, etc)</param>
        public WebsocketClient(Uri url, Func<ClientWebSocket> clientFactory = null)
        {
            Validations.Validations.ValidateInput(url, nameof(url));

            _uri = url;
            _clientFactory = clientFactory ?? (() => new ClientWebSocket
            {
                Options = {KeepAliveInterval = new TimeSpan(0, 0, 5, 0)}
            }); 
        }

        /// <inheritdoc />
        public Uri Url
        {
            get => _uri;
            set
            {
                Validations.Validations.ValidateInput(value, nameof(Url));
                _uri = value;
            }
        }

        /// <summary>
        /// Stream with received message (raw format)
        /// </summary>
        public IObservable<ResponseMessage> MessageReceived => _messageReceivedSubject.AsObservable();

        /// <summary>
        /// Stream for reconnection event (triggered after the new connection) 
        /// </summary>
        public IObservable<ReconnectionType> ReconnectionHappened => _reconnectionSubject.AsObservable();

        /// <summary>
        /// Stream for disconnection event (triggered after the connection was lost) 
        /// </summary>
        public IObservable<DisconnectionType> DisconnectionHappened => _disconnectedSubject.AsObservable();

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if no message comes from server.
        /// Default 60000 ms (1 minute)
        /// </summary>
        public int ReceiveTimeoutMs { get; set; } = 60 * 1000;

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if last reconnection failed.
        /// Default 60000 ms (1 minute)
        /// </summary>
        public int ErrorReconnectTimeoutMs { get; set; } = 10 * 1000;

        /// <summary>
        /// Enable or disable reconnection functionality (enabled by default)
        /// </summary>
        public bool IsReconnectionEnabled { get; set; } = true;

        /// <summary>
        /// Get or set the name of the current websocket client instance.
        /// For logging purpose (in case you use more parallel websocket clients and want to distinguish between them)
        /// </summary>
        public string Name { get; set;}

        /// <summary>
        /// Returns true if Start() method was called at least once. False if not started or disposed
        /// </summary>
        public bool IsStarted { get; private set; }

        /// <inheritdoc />
        public Encoding MessageEncoding { get; set; }

        /// <inheritdoc />
        public ClientWebSocket NativeClient => _client;

        /// <summary>
        /// Terminate the websocket connection and cleanup everything
        /// </summary>
        public void Dispose()
        {
            _disposing = true;
            Logger.Debug(L("Disposing.."));
            try
            {
                //_cancellationTotal?.Cancel();
                _cancelConnectionTask?.Cancel();
                _client?.Abort();
                _client?.Dispose();
                _client = null;
                //_cancellationTotal?.Dispose();
                _cancelConnectionTask.Dispose();
                //_messagesTextToSendQueue?.Dispose();
            }
            catch (Exception e)
            {
                Logger.Error(e, L($"Failed to dispose client, error: {e.Message}"));
            }

            IsStarted = false;
            _disconnectedSubject.OnNext(DisconnectionType.Exit);
        }
       
        /// <summary>
        /// Start listening to the websocket stream on the background thread
        /// </summary>
        public async Task Start()
        {
            if (IsStarted)
            {
                Logger.Debug(L("Client already started, ignoring.."));
                return;
            }
            IsStarted = true;

            Logger.Debug(L("Starting.."));
            //_cancellationTotal = new CancellationTokenSource();
            _cancelConnectionTask = new CancellationTokenSource();

            await StartClient(_uri, _cancelConnectionTask.Token, ReconnectionType.Initial);

            StartBackgroundThreadForSendingText();
            StartBackgroundThreadForSendingBinary();
        }

        /// <inheritdoc />
        public async Task<bool> Stop(WebSocketCloseStatus status, string statusDescription)
        {
            if (_connectionTask != null)
            {
                _cancelConnectionTask.Cancel();
            }

            if (_client == null)
            {
                IsStarted = false;
                return false;
            }
                
            // First send a close request to the server
            if (_client != null)
                await _client.CloseOutputAsync(status, statusDescription, CancellationToken.None);
            // Then HandleConnection() will receive a close response, so that it can continue from ReceiveAsync() and exit.
            if (_connectionTask != null)
            {
                try
                {
                    await _connectionTask;
                }
                catch (TaskCanceledException)
                { }
            }

            IsStarted = false;
            return true;
        }

        /// <summary>
        /// Send text message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on an other thread
        /// </summary>
        /// <param name="message">Text message to be sent</param>
        public ValueTask Send(string message)
        {
            if (!(_client?.State == WebSocketState.Open))
            {
                //if (IsReconnectionEnabled)
                //    Reconnect();
                throw new Exceptions.WebsocketException("websocket not connected");
            }

            Validations.Validations.ValidateInput(message, nameof(message));

            return _messagesTextToSendQueue.Writer.WriteAsync(message);
        }

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on an other thread
        /// </summary>
        /// <param name="message">Binary message to be sent</param>
        public ValueTask Send(byte[] message)
        {
            if (!(_client?.State == WebSocketState.Open))
            {
                throw new Exceptions.WebsocketException("websocket not connected");
            }

            Validations.Validations.ValidateInput(message, nameof(message));

            return _messagesBinaryToSendQueue.Writer.WriteAsync(message);
        }

        /// <summary>
        /// Send text message to the websocket channel. 
        /// It doesn't use a sending queue, 
        /// beware of issue while sending two messages in the exact same time 
        /// on the full .NET Framework platform
        /// </summary>
        /// <param name="message">Message to be sent</param>
        //public Task SendInstant(string message)
        //{
        //    Validations.Validations.ValidateInput(message, nameof(message));

        //    return SendInternal(message);
        //}

        /// <summary>
        /// Send binary message to the websocket channel. 
        /// It doesn't use a sending queue, 
        /// beware of issue while sending two messages in the exact same time 
        /// on the full .NET Framework platform
        /// </summary>
        /// <param name="message">Message to be sent</param>
        //public Task SendInstant(byte[] message)
        //{
        //    return SendInternal(message);
        //}

        /// <summary>
        /// Force reconnection. 
        /// Closes current websocket stream and perform a new connection to the server.
        /// </summary>
        public Task Reconnect()
        {
            if (!IsStarted)
            {
                Logger.Debug(L("Client not started, ignoring reconnection.."));
                return Task.CompletedTask;
            }

            if (_connectionTask != null)
            {
                _cancelConnectionTask.Cancel();
                _cancelConnectionTask = new CancellationTokenSource();
            }
            var completionSource = new TaskCompletionSource<int>();
            _connectionTask = HandleConnection(completionSource, _uri, ReconnectionType.ByUser, _cancelConnectionTask.Token);
            return completionSource.Task;
        }

        private async Task SendTextFromQueue()
        {
            try
            {
                var reader = _messagesTextToSendQueue.Reader;
                while (true)
                {
                    var message = await reader.ReadAsync(_cancelConnectionTask.Token);
                    try
                    {
                        // do not resend here. let the user decide what to do when reconnecting
                        //if (_reconnecting)
                        //{
                        //    await writer.WriteAsync(message);
                        //    await Task.Delay(500);
                        //    continue;
                        //}
                        await SendInternal(message);//.ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, L($"Failed to send text message: '{message}'"));
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // task was canceled, ignore
            }
            catch (OperationCanceledException)
            {
                // operation was canceled, ignore
            }
            catch (Exception e)
            {
                if (_cancelConnectionTask.IsCancellationRequested || _disposing)
                {
                    // disposing/canceling, do nothing and exit
                    return;
                }

                Logger.Trace(L($"Sending text thread failed, error: {e.Message}. Creating a new sending thread."));
                StartBackgroundThreadForSendingText();
            }

        }

        private async Task SendBinaryFromQueue()
        {
            try
            {
                var reader = _messagesBinaryToSendQueue.Reader;
                while (true)
                {
                    var message = await reader.ReadAsync(_cancelConnectionTask.Token);
                    try
                    {
                        // do not resend here. let the user decide what to do when reconnecting
                        //if (_reconnecting)
                        //{
                        //    _messagesBinaryToSendQueue.Add(message);
                        //    await Task.Delay(500);
                        //    continue;
                        //}
                        await SendInternal(message);//.ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, L($"Failed to send binary message: '{message}'."));
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // task was canceled, ignore
            }
            catch (OperationCanceledException)
            {
                // operation was canceled, ignore
            }
            catch (Exception e)
            {
                if (_cancelConnectionTask.IsCancellationRequested || _disposing)
                {
                    // disposing/canceling, do nothing and exit
                    return;
                }

                Logger.Trace(L($"Sending binary thread failed, error: {e.Message}. Creating a new sending thread."));
                StartBackgroundThreadForSendingBinary();
            }

        }

        private void StartBackgroundThreadForSendingText()
        {
            _ = SendTextFromQueue();
        }

        private void StartBackgroundThreadForSendingBinary()
        {
            _ = SendBinaryFromQueue();
        }

        private async Task SendInternal(string message)
        {
            Logger.Trace(L($"Sending:  {message}"));
            var buffer = GetEncoding().GetBytes(message);
            var messageSegment = new ArraySegment<byte>(buffer);
            await _client?.SendAsync(messageSegment, WebSocketMessageType.Text, true, _cancelConnectionTask.Token);
        }

        private async Task SendInternal(byte[] message)
        {
            await _client?.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Binary, true, _cancelConnectionTask.Token);
        }

        private Task StartClient(Uri uri, CancellationToken token, ReconnectionType type)
        {
            var completionSource = new TaskCompletionSource<int>();
            _connectionTask = HandleConnection(completionSource, uri, type, token);
            return completionSource.Task;
        }

        //private async Task<ClientWebSocket> GetClient()
        //{
        //    if (_client == null || (_client.State != WebSocketState.Open && _client.State != WebSocketState.Connecting))
        //    {
        //        await OnDisconnect(ReconnectionType.Lost);
        //    }
        //    return _client;
        //}

        //private async Task OnDisconnect(ReconnectionType type)
        //{
        //    if (_connecting)
        //        return;

        //    if (_disposing)
        //        return;

        //    _connecting = true;
        //    if(type != ReconnectionType.Error)
        //        _disconnectedSubject.OnNext(TranslateTypeToDisconnection(type));

        //    _client?.Abort();
        //    _client?.Dispose();
        //    _client = null;
        //    _cancellation.Cancel();
        //    await Task.Delay(1000);

        //    if (!IsReconnectionEnabled)
        //    {
        //        // reconnection disabled, do nothing
        //        IsStarted = false;
        //        _connecting = false;
        //        return;
        //    }

        //    Logger.Debug(L("Reconnecting..."));
        //    _cancellation = new CancellationTokenSource();
        //    await StartClient(_uri, _cancellation.Token, type);
        //    _connecting = false;
        //}

        private async Task HandleConnection(TaskCompletionSource<int> connectionCompltionSource, Uri uri, ReconnectionType type, CancellationToken token)
        {
            while (true)
            {
                try
                {
                    if (token.IsCancellationRequested)
                    {
                        Logger.Debug(L("HandleConnection() canceled"));
                        return;
                    }

                    state = State.Connecting;
                    if (_client != null)
                        _client.Dispose();
                    var client = _clientFactory();
                    _client = client;
                    await client.ConnectAsync(uri, token);
                    state = State.Connected;
                    //_lastReceivedMsg = DateTime.UtcNow;
                    _reconnectionSubject.OnNext(type);
                    connectionCompltionSource?.SetResult(1);
                    connectionCompltionSource = null;

                    do
                    {
                        var buffer = new ArraySegment<byte>(new byte[8192]);

                        using (var ms = new MemoryStream())
                        {
                            WebSocketReceiveResult result;
                            do
                            {
                                var timeoutTask = Task.Delay(ReceiveTimeoutMs);
                                // To prevent the websocket from aborted state, do not cancel a ReceiveAsync().
                                // Instead check the cancel state after it finishes.
                                var taskResult = client.ReceiveAsync(buffer, CancellationToken.None);
                                token.ThrowIfCancellationRequested();
                                var task = await Task.WhenAny(timeoutTask, taskResult);
                                if (task == timeoutTask)
                                    throw new TimeoutException("Websocket receive timeout!");

                                result = taskResult.Result;
                                if (buffer.Array != null)
                                    ms.Write(buffer.Array, buffer.Offset, result.Count);
                                else
                                    throw new Exception($"Unexpected array == null, type={result.MessageType}");
                            } while (!result.EndOfMessage);

                            ms.Seek(0, SeekOrigin.Begin);

                            ResponseMessage message;
                            if (result.MessageType == WebSocketMessageType.Text)
                            {
                                var data = GetEncoding().GetString(ms.ToArray());
                                message = ResponseMessage.TextMessage(data);
                            }
                            else if (result.MessageType == WebSocketMessageType.Binary)
                            {
                                var data = ms.ToArray();
                                message = ResponseMessage.BinaryMessage(data);
                            }
                            else if (result.MessageType == WebSocketMessageType.Close)
                            {
                                _disconnectedSubject.OnNext(DisconnectionType.ByServer);
                                continue;
                            }
                            else
                            {
                                throw new Exceptions.WebsocketBadInputException("unknown message type");
                            }

                            Logger.Trace(L($"Received:  {message}"));
                            //_lastReceivedMsg = DateTime.UtcNow;
                            _messageReceivedSubject.OnNext(message);
                        }

                    } while (client.State == WebSocketState.Open && !token.IsCancellationRequested);
                }
                //catch (AggregateException e)
                //{
                //    connectionCompltionSource?.SetException(e.InnerException);
                //    connectionCompltionSource = null;
                //    Logger.Info($"HandleConnection() unhandled exception {e}");
                //    return;
                //}
                catch (OperationCanceledException e) // cancellation signaled
                {
                    connectionCompltionSource?.SetException(e);
                    connectionCompltionSource = null;
                    Logger.Debug(L("HandleConnection() canceled 2"));
                    return;
                }
                catch (Exception e)
                {
                    Logger.Error(e, L($"Error while listening to websocket stream, error: '{e.Message}'"));

                    var delay = state == State.Connecting ? ErrorReconnectTimeoutMs : 1000;
                    connectionCompltionSource?.SetException(e);
                    connectionCompltionSource = null;
                    _disconnectedSubject.OnNext(state == State.Connecting ? DisconnectionType.Error : DisconnectionType.Lost);
                    state = State.Disconnected;

                    if (_disposing)
                    {
                        Logger.Debug(L("HandleConnection() exit due to disposing"));
                        return;
                    }

                    if (!IsReconnectionEnabled)
                    {
                        Logger.Debug(L("HandleConnection() exit due to error and IsReconnectionEnabled=false"));
                        return;
                    }

                    state = State.Connecting;
                    type = state == State.Connecting ? ReconnectionType.Error : ReconnectionType.Lost;
                    await Task.Delay(delay, token);
                }
            }
        }

        private Encoding GetEncoding()
        {
            if (MessageEncoding == null)
                MessageEncoding = Encoding.UTF8;
            return MessageEncoding;
        }

        private string L(string msg)
        {
            var name = Name ?? "CLIENT";
            return $"[WEBSOCKET {name}] {msg}";
        }

        private DisconnectionType TranslateTypeToDisconnection(ReconnectionType type)
        {
            // beware enum indexes must correspond to each other
            return (DisconnectionType) type;
        }
    }
}
