// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.WorkDispatcher;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Threading;

namespace BuildXL.Scheduler.Distribution
{
    /// <summary>
    /// Handles choose worker computation for <see cref="Scheduler"/>
    /// </summary>
    internal abstract class ChooseWorkerContext
    {
        /// <summary>
        /// The number of times a pip was blocked from acquiring a worker due to resource limits
        /// </summary>
        public int ChooseBlockedCount;

        /// <summary>
        /// The number of times a pip successfully acquired a worker
        /// </summary>
        public int ChooseSuccessCount;

        protected readonly LoggingContext LoggingContext;

        protected readonly IPipQueue PipQueue;

        protected readonly IReadOnlyList<Worker> Workers;

        /// <summary>
        /// Tracks a sequence number in order to verify if workers resources have changed since
        /// the time it was checked. This is used by ChooseWorker to decide if the worker queue can
        /// be paused
        /// </summary>
        protected int WorkerEnableSequenceNumber = 0;

        /// <summary>
        /// Whether the current BuildXL instance serves as a orchestrator node in the distributed build and has workers attached.
        /// </summary>
        protected bool AnyRemoteWorkers => Workers.Count > 1;

        protected readonly LocalWorker LocalWorker;

        protected readonly DispatcherKind Kind;

        protected readonly int MaxParallelDegree;

        private readonly ReadWriteLock m_chooseWorkerTogglePauseLock = ReadWriteLock.Create();

        private readonly bool m_moduleAffinityEnabled;

        protected ChooseWorkerContext(
            LoggingContext loggingContext,
            IReadOnlyList<Worker> workers,
            IPipQueue pipQueue,
            DispatcherKind kind,
            int maxParallelDegree,
            bool moduleAffinityEnabled)
        {
            Workers = workers;
            PipQueue = pipQueue;
            LocalWorker = (LocalWorker)workers[0];
            LoggingContext = loggingContext;
            Kind = kind;
            MaxParallelDegree = maxParallelDegree;
            m_moduleAffinityEnabled = moduleAffinityEnabled;

            foreach (var worker in Workers)
            {
                worker.ResourcesChanged += OnWorkerResourcesChanged;
            }
        }

        public async Task<Worker> ChooseWorkerAsync(RunnablePip runnablePip)
        {
            var worker = await ChooseWorkerCore(runnablePip);

            if (worker == null)
            {
                runnablePip.IsWaitingForWorker = true;
                Interlocked.Increment(ref ChooseBlockedCount);

                // Attempt to pause the choose worker queue since resources are not available
                // Do not pause choose worker queue when module affinity is enabled.
                if (!EngineEnvironmentSettings.DoNotPauseChooseWorkerThreads && !m_moduleAffinityEnabled)
                {
                    TogglePauseChooseWorkerQueue(pause: true, blockedPip: runnablePip);
                }
            }
            else
            {
                runnablePip.IsWaitingForWorker = false;
                Interlocked.Increment(ref ChooseSuccessCount);

                // Ensure the queue is unpaused if we managed to choose a worker
                TogglePauseChooseWorkerQueue(pause: false);
            }

            return worker;
        }

        /// <summary>
        /// Choose a worker
        /// </summary>
        protected abstract Task<Worker> ChooseWorkerCore(RunnablePip runnablePip);

        protected bool MustRunOnOrchestrator(RunnablePip runnablePip)
        {
            if (!AnyRemoteWorkers)
            {
                return true;
            }

            return runnablePip.PipType == PipType.Ipc && ((IpcPip)runnablePip.Pip).MustRunOnOrchestrator;
        }

        public void TogglePauseChooseWorkerQueue(bool pause, RunnablePip blockedPip = null)
        {
            Contract.Requires(pause == (blockedPip != null), "Must specify blocked pip if and only if pausing the choose worker queue");

            if (pause)
            {
                if (blockedPip.IsLight)
                {
                    // Light pips do not block the chooseworker queue.
                    return;
                }

                using (m_chooseWorkerTogglePauseLock.AcquireWriteLock())
                {
                    // Compare with the captured sequence number before the pip re-entered the queue
                    // to avoid race conditions where pip cannot acquire worker resources become available then queue is paused
                    // potentially indefinitely (not likely but theoretically possilbe)
                    if (Volatile.Read(ref WorkerEnableSequenceNumber) == blockedPip.ChooseWorkerSequenceNumber)
                    {
                        SetQueueMaxParallelDegree(0);
                    }
                }
            }
            else
            {
                using (m_chooseWorkerTogglePauseLock.AcquireReadLock())
                {
                    // Update the sequence number. This essentially is called for every increase in resources
                    // and successful acquisition of workers to track changes in resource state that invalidate
                    // decision to pause choose worker queue.
                    Interlocked.Increment(ref WorkerEnableSequenceNumber);

                    // Unpause the queue
                    SetQueueMaxParallelDegree(MaxParallelDegree);
                }
            }
        }

        private void OnWorkerResourcesChanged(Worker worker, WorkerResource resourceKind, bool increased)
        {
            if (increased)
            {
                TogglePauseChooseWorkerQueue(pause: false);
            }
        }

        private void SetQueueMaxParallelDegree(int maxConcurrency)
        {
            PipQueue.SetMaxParallelDegreeByKind(Kind, maxConcurrency);
        }
    }
}
