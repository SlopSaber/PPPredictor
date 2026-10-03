using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace PPPredictor.Utilities
{
    internal static class ImagePreparationWorker
    {
        private enum Operation
        {
            Resize,
            ReadResource,
            ReadPreviewResource,
            PrewarmPreviewResource
        }

        private sealed class Request
        {
            private readonly Operation operation;
            private readonly byte[] data;
            private readonly int width;
            private readonly int height;
            private readonly string resourceName;
            internal readonly TaskCompletionSource<byte[]> Completion =
                new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Request(byte[] data, int width, int height)
            {
                operation = Operation.Resize;
                this.data = data;
                this.width = width;
                this.height = height;
            }

            internal Request(string resourceName, Operation operation = Operation.ReadResource)
            {
                this.operation = operation;
                this.resourceName = resourceName;
            }

            internal void Prepare()
            {
                try
                {
                    byte[] result;
                    switch (operation)
                    {
                        case Operation.Resize:
                            result = DisplayHelper.ResizeImageCore(data, width, height);
                            break;
                        case Operation.ReadPreviewResource:
                            result = ReadPreviewResourceCore(resourceName, true);
                            break;
                        case Operation.PrewarmPreviewResource:
                            ReadPreviewResourceCore(resourceName, false);
                            result = null;
                            break;
                        default:
                            result = ReadResource(resourceName);
                            break;
                    }
                    Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    if (operation == Operation.PrewarmPreviewResource)
                        Completion.TrySetResult(null);
                    else
                        Completion.TrySetException(ex);
                }
            }
        }

        private static readonly object gate = new object();
        private static readonly Queue<Request> requests = new Queue<Request>();
        private static readonly string[] previewResourceNames =
        {
            "PPPredictor.Resources.LeaderBoardLogos.ScoreSaber.png",
            "PPPredictor.Resources.LeaderBoardLogos.BeatLeader.png",
            "PPPredictor.Resources.LeaderBoardLogos.HitBloq.png",
            "PPPredictor.Resources.LeaderBoardLogos.AccSaber.png"
        };
        private static readonly Dictionary<string, byte[]> previewResources = new Dictionary<string, byte[]>();
        private static Task worker;

        // The response buffer is exclusively owned until the queued request finishes.
        internal static Task<byte[]> ResizeAsync(byte[] data, int width, int height)
        {
            return Enqueue(new Request(data, width, height));
        }

        internal static Task<byte[]> ReadResourceAsync(string resourceName)
        {
            return Enqueue(new Request(resourceName));
        }

        internal static void PrewarmPreviewResources()
        {
            foreach (string name in previewResourceNames)
                Enqueue(new Request(name, Operation.PrewarmPreviewResource));
        }

        internal static byte[] ReadPreviewResource(string resourceName)
        {
            Task<byte[]> completion = Enqueue(new Request(resourceName, Operation.ReadPreviewResource));
            // A completed signal cannot inline the physical resource read on this caller.
            if (!completion.IsCompleted)
                ((IAsyncResult)completion).AsyncWaitHandle.WaitOne();
            return completion.GetAwaiter().GetResult();
        }

        private static Task<byte[]> Enqueue(Request request)
        {
            lock (gate)
            {
                requests.Enqueue(request);
                if (worker == null)
                    StartWorker();
            }
            return request.Completion.Task;
        }

        private static void StartWorker()
        {
            if (ExecutionContext.IsFlowSuppressed())
            {
                ScheduleWorker();
                return;
            }

            using (ExecutionContext.SuppressFlow())
                ScheduleWorker();
        }

        private static void ScheduleWorker()
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
                request.Prepare();
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

        private static byte[] ReadPreviewResourceCore(string resourceName, bool copy)
        {
            bool cacheable = Array.IndexOf(previewResourceNames, resourceName) >= 0;
            byte[] data;
            if (!cacheable || !previewResources.TryGetValue(resourceName, out data))
            {
                data = ReadResource(resourceName, true);
                if (cacheable && data != null)
                    previewResources[resourceName] = data;
            }
            return data == null || !copy ? null : (byte[])data.Clone();
        }

        private static byte[] ReadResource(string resourceName, bool optional = false)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null && optional)
                    return null;
                byte[] data = new byte[stream.Length];
                int offset = 0;
                while (offset < data.Length)
                {
                    int count = stream.Read(data, offset, data.Length - offset);
                    if (count == 0)
                        return null;
                    offset += count;
                }
                return data;
            }
        }
    }
}
