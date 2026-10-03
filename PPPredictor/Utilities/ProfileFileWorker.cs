using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace PPPredictor.Utilities
{
    internal static class ProfileFileWorker
    {
        private enum Operation
        {
            Exists,
            Read,
            Write
        }

        private sealed class Request
        {
            internal readonly Operation operation;
            internal readonly string path;
            internal readonly string text;
            internal readonly long version;
            internal readonly TaskCompletionSource<Result> completion;

            internal Request(Operation operation, string path, string text, long version)
            {
                this.operation = operation;
                this.path = path;
                this.text = text;
                this.version = version;
                completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private sealed class Result
        {
            internal readonly long version;
            internal bool exists;
            internal string text;
            internal Exception error;

            internal Result(long version)
            {
                this.version = version;
            }
        }

        private static readonly object gate = new object();
        private static readonly Queue<Request> requests = new Queue<Request>();
        private static Task worker;
        private static long nextVersion;

        internal static bool Exists(string path)
        {
            return Complete(Queue(Operation.Exists, path)).exists;
        }

        internal static string Read(string path)
        {
            return Complete(Queue(Operation.Read, path)).text;
        }

        internal static void Write(string path, string ownedText)
        {
            Complete(Queue(Operation.Write, path, ownedText));
        }

        private static Request Queue(Operation operation, string path, string text = null)
        {
            lock (gate)
            {
                Request request = new Request(operation, path, text, ++nextVersion);
                requests.Enqueue(request);
                if (worker == null)
                    StartWorker();
                return request;
            }
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
            worker = Task.Factory.StartNew(ProcessQueue, CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            worker.ContinueWith(Completed, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static void ProcessQueue()
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

                Result result = new Result(request.version);
                try
                {
                    switch (request.operation)
                    {
                        case Operation.Exists:
                            result.exists = File.Exists(request.path);
                            break;
                        case Operation.Read:
                            result.text = File.ReadAllText(request.path);
                            break;
                        case Operation.Write:
                            File.WriteAllText(request.path, request.text);
                            break;
                    }
                }
                catch (Exception exception)
                {
                    result.error = exception;
                }

                request.completion.SetResult(result);
            }
        }

        private static void Completed(Task completed)
        {
            lock (gate)
            {
                if (worker != completed)
                    return;
                worker = null;
                if (requests.Count != 0)
                    StartWorker();
            }
        }

        private static Result Complete(Request request)
        {
            Task<Result> task = request.completion.Task;
            // Wait on the task's completion signal before consuming its result.
            if (!task.IsCompleted)
                ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
            Result result = task.GetAwaiter().GetResult();
            if (result.version != request.version)
                throw new InvalidOperationException("Unexpected profile persistence result version.");
            if (result.error != null)
                ExceptionDispatchInfo.Capture(result.error).Throw();
            return result;
        }
    }
}
