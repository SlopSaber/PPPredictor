using WebSocketSharp;
using WebSocketSharp.Server;
using System;

namespace PPPredictor.WebSocket
{
    internal class WebSocketOverlayServer
    {
        private static WebSocketOverlayServer activeServer;
        private WebSocketServer server;

        public void StartSocket()
        {
            // AppCore is recreated when Beat Saber applies settings. Its new
            // manager can start before the old manager is disposed.
            activeServer?.CloseSocket();

            WebSocketServer nextServer = null;
            try
            {
                nextServer = new WebSocketServer($"ws://localhost:{Plugin.ProfileInfo.StreamOverlayPort}");
                nextServer.AddWebSocketService<PPPreditorWS>("/socket");
                nextServer.Start();
                server = nextServer;
                activeServer = this;
            }
            catch (Exception ex)
            {
                try
                {
                    nextServer?.Stop();
                }
                catch (Exception stopEx)
                {
                    Plugin.Log?.Warn($"PPPredictor overlay listener cleanup failed: {stopEx}");
                }
                Plugin.Log?.Warn($"PPPredictor overlay listener could not start on port {Plugin.ProfileInfo.StreamOverlayPort}: {ex}");
            }
        }

        public void CloseSocket()
        {
            WebSocketServer oldServer = server;
            server = null;
            if (ReferenceEquals(activeServer, this))
                activeServer = null;
            try
            {
                oldServer?.Stop();
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"PPPredictor overlay listener could not stop: {ex}");
            }
        }

        public void SendData(string s)
        {
            if (server != null && server.IsListening)
            {
                server.WebSocketServices["/socket"].Sessions.Broadcast(s);
            }
        }
    }

    internal class PPPreditorWS : WebSocketBehavior
    {
        protected override void OnMessage(MessageEventArgs e)
        {
        }
    }
}
