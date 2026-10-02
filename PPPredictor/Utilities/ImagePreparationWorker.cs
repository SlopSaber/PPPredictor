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
            ReadResource
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

            internal Request(string resourceName)
            {
                operation = Operation.ReadResource;
                this.resourceName = resourceName;
            }

            internal void Prepare()
            {
                try
                {
                    byte[] result = operation == Operation.Resize
                        ? DisplayHelper.ResizeImageCore(data, width, height)
                        : ReadResource(resourceName);
                    Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    Completion.TrySetException(ex);
                }
            }
        }

        private static readonly object gate = new object();
        private static readonly Queue<Request> requests = new Queue<Request>();
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

        private static byte[] ReadResource(string resourceName)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
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
