using Newtonsoft.Json;
using PPPredictor.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp;

namespace PPPredictor.WebSocket
{
    internal static class ScoreSocketTransportWorker
    {
        private sealed class Connection
        {
            private readonly long identifier;
            private readonly string leaderboard;
            private readonly Type dataType;
            private readonly CultureInfo culture;
            private readonly CultureInfo uiCulture;
            private readonly WebSocketSharp.WebSocket socket;

            internal Connection(long identifier, string url, string leaderboard, Type dataType,
                CultureInfo culture, CultureInfo uiCulture)
            {
                this.identifier = identifier;
                this.leaderboard = leaderboard;
                this.dataType = dataType;
                this.culture = culture;
                this.uiCulture = uiCulture;
                socket = new WebSocketSharp.WebSocket(url);
                socket.SslConfiguration.EnabledSslProtocols = SslProtocols.Tls12;
                socket.OnMessage += OnMessage;
                socket.OnError += OnError;
            }

            internal void Connect()
            {
                socket.Connect();
            }

            internal void Stop(bool keepConnection)
            {
                socket.OnMessage -= OnMessage;
                socket.OnError -= OnError;
                if (!keepConnection)
                    socket.Close();
            }

            private void OnMessage(object sender, MessageEventArgs args)
            {
                CultureInfo previous = CultureInfo.CurrentCulture;
                CultureInfo previousUi = CultureInfo.CurrentUICulture;
                try
                {
                    CultureInfo.CurrentCulture = culture;
                    CultureInfo.CurrentUICulture = uiCulture;
                    var raw = (IPPPRawWebsocketData)JsonConvert.DeserializeObject(args.Data, dataType);
                    var data = raw.ConvertToPPPWebSocketData(leaderboard);
                    ScoreSocketOwnerCallbacks.PostScore(identifier, data);
                }
                catch (Exception ex)
                {
                    if (args != null && args.Data == "Connected to the ScoreSaber WSS") return;
                    ScoreSocketOwnerCallbacks.PostMessageError(identifier, ex.Message);
                }
                finally
                {
                    CultureInfo.CurrentCulture = previous;
                    CultureInfo.CurrentUICulture = previousUi;
                }
            }

            private void OnError(object sender, ErrorEventArgs args)
            {
                ScoreSocketOwnerCallbacks.PostError(identifier);
            }
        }

        private sealed class Request
        {
            private readonly bool start;
            private readonly long identifier;
            private readonly string url;
            private readonly string leaderboard;
            private readonly Type dataType;
            private readonly CultureInfo culture;
            private readonly CultureInfo uiCulture;
            private readonly bool keepConnection;

            internal Request(long identifier, string url, string leaderboard, Type dataType,
                CultureInfo culture, CultureInfo uiCulture)
            {
                start = true;
                this.identifier = identifier;
                this.url = url;
                this.leaderboard = leaderboard;
                this.dataType = dataType;
                this.culture = culture;
                this.uiCulture = uiCulture;
            }

            internal Request(long identifier, string leaderboard, bool keepConnection)
            {
                this.identifier = identifier;
                this.leaderboard = leaderboard;
                this.keepConnection = keepConnection;
            }

            internal void Process()
            {
                if (start)
                {
                    CultureInfo previous = CultureInfo.CurrentCulture;
                    CultureInfo previousUi = CultureInfo.CurrentUICulture;
                    try
                    {
                        CultureInfo.CurrentCulture = culture;
                        CultureInfo.CurrentUICulture = uiCulture;
                        var createdConnection = new Connection(identifier, url, leaderboard, dataType, culture, uiCulture);
                        connections.Add(identifier, createdConnection);
                        createdConnection.Connect();
                    }
                    catch (Exception ex)
                    {
                        ScoreSocketOwnerCallbacks.PostCreationError(identifier, ex.Message);
                    }
                    finally
                    {
                        CultureInfo.CurrentCulture = previous;
                        CultureInfo.CurrentUICulture = previousUi;
                    }
                }
                else if (connections.TryGetValue(identifier, out Connection connection))
                {
                    connections.Remove(identifier);
                    try
                    {
                        connection.Stop(keepConnection);
                    }
                    catch (Exception ex)
                    {
                        ScoreSocketOwnerCallbacks.PostCleanupError($"Error closing Websocket for {leaderboard}: {ex.Message}");
                    }
                }
            }
        }

        private static readonly object gate = new object();
        private static readonly Queue<Request> requests = new Queue<Request>();
        private static readonly Dictionary<long, Connection> connections = new Dictionary<long, Connection>();
        private static Task worker;

        internal static void Start(long identifier, string url, string leaderboard, Type dataType,
            CultureInfo culture, CultureInfo uiCulture)
        {
            Enqueue(new Request(identifier, url, leaderboard, dataType, culture, uiCulture));
        }

        internal static void Stop(long identifier, string leaderboard, bool keepConnection)
        {
            Enqueue(new Request(identifier, leaderboard, keepConnection));
        }

        private static void Enqueue(Request request)
        {
            lock (gate)
            {
                requests.Enqueue(request);
                if (worker == null)
                    StartWorker();
            }
        }

        private static void StartWorker()
        {
            worker = Task.Factory.StartNew(Drain, CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            worker.ContinueWith(WorkerCompleted, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static void Drain()
        {
            while (true)
            {
                Request request;
                lock (gate)
                {
                    if (requests.Count == 0)
                        return;
                    request = requests.Dequeue();
                }
                request.Process();
            }
        }

        private static void WorkerCompleted(Task completed)
        {
            lock (gate)
            {
                if (!ReferenceEquals(worker, completed))
                    return;
                worker = null;
                if (requests.Count != 0)
                    StartWorker();
            }
        }
    }
}
