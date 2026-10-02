using WebSocketSharp;
using WebSocketSharp.Server;
using Newtonsoft.Json;
using PPPredictor.Utilities;
using System.Threading;

namespace PPPredictor.WebSocket
{
    internal class WebSocketOverlayServer
    {
        private static long nextIdentifier;
        private readonly long identifier = Interlocked.Increment(ref nextIdentifier);

        public void StartSocket()
        {
            string port = Plugin.ProfileInfo.StreamOverlayPort.ToString();
            OverlayTransportWorker.Start(identifier, $"ws://localhost:{port}", port);
        }

        public void CloseSocket()
        {
            OverlayTransportWorker.Stop(identifier);
        }

        public void SendData(string s)
        {
            OverlayTransportWorker.Send(identifier, s);
        }

        // The caller transfers its new packet and scalar-only payload list.
        internal void SendData(MessageContainer message)
        {
            if (JsonConvert.DefaultSettings != null)
                SendData(JsonConvert.SerializeObject(message));
            else
                OverlayTransportWorker.Send(identifier, message);
        }
    }

    internal class PPPreditorWS : WebSocketBehavior
    {
        protected override void OnMessage(MessageEventArgs e)
        {
        }
    }
}
