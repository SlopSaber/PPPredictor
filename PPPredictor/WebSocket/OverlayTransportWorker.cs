using IPA.Utilities.Async;
using Newtonsoft.Json;
using PPPredictor.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp.Server;

namespace PPPredictor.WebSocket
{
    internal static class OverlayTransportWorker
    {
        private enum Operation
        {
            Start,
            Stop,
            SendText,
            SendMessage
        }

        private sealed class Request
        {
            private readonly Operation operation;
            private readonly long identifier;
            private readonly string url;
            private readonly string port;
            private readonly string text;
            private readonly MessageContainer message;

            internal Request(long identifier, string url, string port)
            {
                operation = Operation.Start;
                this.identifier = identifier;
                this.url = url;
                this.port = port;
            }

            internal Request(long identifier)
            {
                operation = Operation.Stop;
                this.identifier = identifier;
            }

            internal Request(long identifier, string text)
            {
                operation = Operation.SendText;
                this.identifier = identifier;
                this.text = text;
            }

            internal Request(long identifier, MessageContainer message)
            {
                operation = Operation.SendMessage;
                this.identifier = identifier;
                this.message = message;
            }

            internal void Process()
            {
                if (operation == Operation.Start)
                {
                    StartServer(identifier, url, port);
                }
                else if (operation == Operation.Stop)
                {
                    if (identifier == activeIdentifier)
                        StopServer();
                }
                else if (identifier == activeIdentifier && server != null && server.IsListening)
                {
                    try
                    {
                        string data = operation == Operation.SendText ? text : Serialize(message);
                        server.WebSocketServices["/socket"].Sessions.Broadcast(data);
                    }
                    catch (Exception ex)
                    {
                        Warn($"PPPredictor overlay listener could not send: {ex}");
                    }
                }
            }
        }

        private static readonly object gate = new object();
        private static readonly Queue<Request> requests = new Queue<Request>();
        private static Task worker;
        private static long activeIdentifier;
        private static WebSocketServer server;

        internal static void Start(long identifier, string url, string port)
        {
            Enqueue(new Request(identifier, url, port));
        }

        internal static void Stop(long identifier)
        {
            Enqueue(new Request(identifier));
        }

        internal static void Send(long identifier, string text)
        {
            Enqueue(new Request(identifier, text));
        }

        internal static void Send(long identifier, MessageContainer message)
        {
            Enqueue(new Request(identifier, message));
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

        private static void StartServer(long identifier, string url, string port)
        {
            StopServer();
            WebSocketServer nextServer = null;
            try
            {
                nextServer = new WebSocketServer(url);
                nextServer.AddWebSocketService<PPPreditorWS>("/socket");
                nextServer.Start();
                server = nextServer;
                activeIdentifier = identifier;
            }
            catch (Exception ex)
            {
                try
                {
                    nextServer?.Stop();
                }
                catch (Exception stopEx)
                {
                    Warn($"PPPredictor overlay listener cleanup failed: {stopEx}");
                }
                Warn($"PPPredictor overlay listener could not start on port {port}: {ex}");
            }
        }

        private static void StopServer()
        {
            WebSocketServer oldServer = server;
            server = null;
            activeIdentifier = 0;
            try
            {
                oldServer?.Stop();
            }
            catch (Exception ex)
            {
                Warn($"PPPredictor overlay listener could not stop: {ex}");
            }
        }

        private static string Serialize(MessageContainer message)
        {
            var serializer = JsonSerializer.Create();
            var buffer = new StringBuilder(256);
            using (var textWriter = new StringWriter(buffer, CultureInfo.InvariantCulture))
            using (var jsonWriter = new JsonTextWriter(textWriter))
            {
                jsonWriter.Formatting = serializer.Formatting;
                serializer.Serialize(jsonWriter, message, null);
            }
            return buffer.ToString();
        }

        private static void Warn(string message)
        {
            try
            {
                _ = UnityMainThreadTaskScheduler.Factory.StartNew(() => Plugin.Log?.Warn(message));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (TaskSchedulerException)
            {
            }
        }
    }
}
