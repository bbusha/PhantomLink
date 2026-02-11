using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace PhantomLink.Core
{
    /// <summary>
    /// IPC Client for communicating with MelonLoader pipe mod
    /// Handles all communication between external tool and game mod
    /// </summary>
    public static class IPCMeloaderClient
    {
        private static NamedPipeClientStream _pipeClient;
        private static StreamReader _reader;
        private static StreamWriter _writer;
        private static bool _isConnected = false;
        private static readonly object _syncLock = new object();
        private static Thread _reconnectThread;
        private static bool _isRunning = false;
        private static string _pipeName = "PhantomLinkUniversalPipe";
        private static readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private static DateTime _nextConnectWaitLogUtc = DateTime.MinValue;
        private static DateTime _nextNotConnectedWarnUtc = DateTime.MinValue;
        private static DateTime _nextConnectErrorLogUtc = DateTime.MinValue;
        private static string _lastConnectErrorMessage = null;

        /// <summary>
        /// Event raised when connection status changes
        /// </summary>
        public static event Action<bool> ConnectionStatusChanged;

        public sealed class IPCEditEvent
        {
            public string Command { get; set; }
            public string Response { get; set; }
            public DateTime Utc { get; set; }
            public bool Success { get; set; }
        }

        public static event Action<IPCEditEvent> EditPerformed;

        /// <summary>
        /// Initialize and start the IPC client
        /// </summary>
        public static void Start()
        {
            if (_isRunning) return;

            lock (_syncLock)
            {
                _isRunning = true;
                
                // Start reconnect thread
                _reconnectThread = new Thread(ReconnectThread)
                {
                    Name = "IPCReconnectThread",
                    IsBackground = true
                };
                _reconnectThread.Start();

                Logger.LogInfo($"IPC Meloader Client started (pipe: '{_pipeName}')");
            }
        }

        /// <summary>
        /// Stop the IPC client and clean up resources
        /// </summary>
        public static void Stop()
        {
            lock (_syncLock)
            {
                _isRunning = false;
                Disconnect();
                
                if (_reconnectThread != null && _reconnectThread.IsAlive)
                {
                    if (!_reconnectThread.Join(2000))
                    {
                        Logger.LogWarning("IPC reconnect thread did not terminate gracefully");
                    }
                }

                Logger.LogInfo("IPC Meloader Client stopped");
            }
        }

        /// <summary>
        /// Send command to MelonLoader mod and receive response
        /// </summary>
        public static async Task<string> SendCommandAsync(string command, int timeoutMs = 5000)
        {
            if (!_isConnected)
            {
                var now = DateTime.UtcNow;
                if (now >= _nextNotConnectedWarnUtc)
                {
                    var preview = command ?? "";
                    if (preview.Length > 120)
                        preview = preview.Substring(0, 120) + "...";
                    Logger.LogWarning($"[IPC_SEND] Not connected - cannot send: {preview}");
                    _nextNotConnectedWarnUtc = now.AddSeconds(5);
                }
                return "ERROR|Not connected to MelonLoader mod";
            }

            var sendLockTaken = false;
            try
            {
                var cts = new CancellationTokenSource(timeoutMs);

                await _sendLock.WaitAsync(cts.Token);
                sendLockTaken = true;
                
                // Check connection status outside lock to avoid deadlocks
                if (!_isConnected)
                {
                    return "ERROR|Not connected to MelonLoader mod";
                }

                // Perform write/read operations without holding the lock
                await _writer.WriteLineAsync(command);
                
                // Read response with timeout
                string response = await _reader.ReadLineAsync().WaitAsync(cts.Token);
                response ??= "ERROR|No response received";

                if (IsEditCommand(command))
                {
                    try
                    {
                        var ok = response.StartsWith("SUCCESS|", StringComparison.Ordinal) ||
                                 response.StartsWith("DISCOVERY_CHEAT_BIND|", StringComparison.Ordinal) ||
                                 response.Contains("|ok=1", StringComparison.OrdinalIgnoreCase);

                        EditPerformed?.Invoke(new IPCEditEvent
                        {
                            Command = command,
                            Response = response,
                            Utc = DateTime.UtcNow,
                            Success = ok
                        });
                    }
                    catch
                    {
                    }
                }

                return response;
            }
            catch (OperationCanceledException)
            {
                return "ERROR|Command timeout";
            }
            catch (Exception ex)
            {
                Logger.LogError($"[IPC_SEND] Failed to send command: {command} - Error: {ex.Message}");
                Logger.LogError($"[IPC_SEND] Stack trace: {ex.StackTrace}");
                Disconnect();
                return $"ERROR|{ex.Message}";
            }
            finally
            {
                if (sendLockTaken)
                {
                    _sendLock.Release();
                }
            }
        }

        private static bool IsEditCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return false;
            if (command.StartsWith("SET_FIELD|", StringComparison.Ordinal) ||
                command.StartsWith("SET_PROPERTY|", StringComparison.Ordinal) ||
                command.StartsWith("SET_FIELD_INSTANCE|", StringComparison.Ordinal) ||
                command.StartsWith("SET_PROPERTY_INSTANCE|", StringComparison.Ordinal) ||
                command.StartsWith("DISCOVERY_CHEAT_BIND|", StringComparison.Ordinal) ||
                command.StartsWith("DISCOVERY_CHEAT_UNBIND|", StringComparison.Ordinal))
                return true;
            return false;
        }

        /// <summary>
        /// Check if currently connected to MelonLoader mod
        /// </summary>
        public static bool IsConnected
        {
            get { lock (_syncLock) return _isConnected && _pipeClient != null; }
        }

        private static void ReconnectThread()
        {
            string lastError = null;

            while (_isRunning)
            {
                try
                {
                    if (!IsConnected)
                    {
                        Connect();
                    }
                    
                    Thread.Sleep(1000); // Check connection every second
                }
                catch (Exception ex)
                {
                    var message = ex.Message ?? ex.GetType().Name;
                    if (!string.Equals(lastError, message, StringComparison.Ordinal))
                    {
                        Logger.LogError($"[IPC_RECONNECT] Thread error: {ex.GetType().Name}: {message}");
                        lastError = message;
                    }
                    Thread.Sleep(5000); // Wait longer on error
                }
            }

        }

        private static void Connect()
        {
            NamedPipeClientStream client = null;
            try
            {
                client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(3000);

                Action<bool> handler;
                lock (_syncLock)
                {
                    DisconnectLocked(raiseEvent: false);

                    if (!_isRunning)
                    {
                        try { client.Dispose(); } catch { }
                        return;
                    }

                    _pipeClient = client;
                    _reader = new StreamReader(_pipeClient);
                    _writer = new StreamWriter(_pipeClient) { AutoFlush = true };
                    _isConnected = true;
                    handler = ConnectionStatusChanged;
                }

                Logger.LogInfo("Connected to MelonLoader mod");
                handler?.Invoke(true);
                return;
            }
            catch (TimeoutException)
            {
                var now = DateTime.UtcNow;
                if (now >= _nextConnectWaitLogUtc)
                {
                    Logger.LogInfo("Waiting for MelonLoader mod connection...");
                    _nextConnectWaitLogUtc = now.AddSeconds(30);
                }
            }
            catch (Exception ex)
            {
                var now = DateTime.UtcNow;
                var message = ex.Message ?? ex.GetType().Name;
                if (!string.Equals(_lastConnectErrorMessage, message, StringComparison.Ordinal) || now >= _nextConnectErrorLogUtc)
                {
                    Logger.LogError($"[IPC_CONNECT] Failed to connect: {ex.GetType().Name}: {message}");
                    _lastConnectErrorMessage = message;
                    _nextConnectErrorLogUtc = now.AddSeconds(30);
                }
            }
            finally
            {
                try
                {
                    if (client != null && !ReferenceEquals(client, _pipeClient))
                    {
                        client.Dispose();
                    }
                }
                catch
                {
                }
            }
        }

        private static void Disconnect()
        {
            Action<bool> handler;
            var shouldRaise = false;
            lock (_syncLock)
            {
                shouldRaise = _isConnected;
                DisconnectLocked(raiseEvent: false);
                handler = ConnectionStatusChanged;
            }

            if (shouldRaise)
            {
                handler?.Invoke(false);
            }
        }

        private static void DisconnectLocked(bool raiseEvent)
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipeClient?.Dispose(); } catch { }

            _writer = null;
            _reader = null;
            _pipeClient = null;
            _isConnected = false;

            if (raiseEvent)
            {
                ConnectionStatusChanged?.Invoke(false);
            }
        }

        /// <summary>
        /// Try to parse a successful response from pipe command
        /// </summary>
        public static bool TryParseSuccess(string response, out string result)
        {
            result = null;
            if (string.IsNullOrEmpty(response))
                return false;

            if (!response.StartsWith("ERROR|"))
            {
                result = response;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Try to parse an error response from pipe command
        /// </summary>
        public static bool TryParseError(string response, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(response))
                return false;

            if (response.StartsWith("ERROR|"))
            {
                error = response.Substring("ERROR|".Length);
                return true;
            }

            return false;
        }
    }
}
