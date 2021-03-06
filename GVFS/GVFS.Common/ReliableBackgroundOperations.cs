﻿using GVFS.Common.Tracing;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Isam.Esent.Collections.Generic;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GVFS.Common
{
    public class ReliableBackgroundOperations<TBackgroundOperation> : IDisposable where TBackgroundOperation : IBackgroundOperation
    {
        public const long UnassignedOperationId = 0;

        private const int ActionRetryDelayMS = 50;
        private const int MaxCallbackAttemptsOnShutdown = 5;
        private const int LogUpdateTaskThreshold = 25000;
        private static readonly string EtwArea = "ProcessBackgroundOperations";

        private long lastOperationId;
        private PersistentDictionary<long, TBackgroundOperation> persistence;

        private ConcurrentQueue<TBackgroundOperation> backgroundOperations;
        private AutoResetEvent wakeUpThread;
        private Task backgroundThread;
        private bool isStopping;

        private GVFSContext context;

        // TODO 656051: Replace these callbacks with an interface
        private Func<CallbackResult> preCallback;
        private Func<TBackgroundOperation, CallbackResult> callback;
        private Func<CallbackResult> postCallback;

        public ReliableBackgroundOperations(
            GVFSContext context,
            Func<CallbackResult> preCallback,             
            Func<TBackgroundOperation, CallbackResult> callback,
            Func<CallbackResult> postCallback,
            string databaseName)
        {
            this.persistence = new PersistentDictionary<long, TBackgroundOperation>(
                Path.Combine(context.Enlistment.DotGVFSRoot, databaseName));

            this.backgroundOperations = new ConcurrentQueue<TBackgroundOperation>();
            this.wakeUpThread = new AutoResetEvent(true);

            this.context = context;
            this.preCallback = preCallback;
            this.callback = callback;
            this.postCallback = postCallback;
            this.lastOperationId = UnassignedOperationId;

            // Enqueue saved oeprations here in the constructor to ensure that this.lastOperationId is
            // set properly before any new operations are queued
            this.EnqueueSavedOperations();
        }

        private enum AcquireGVFSLockResult
        {
            LockAcquired,
            ShuttingDown
        }

        public int Count
        {
            get { return this.backgroundOperations.Count; }
        }

        public void Start()
        {            
            this.backgroundThread = Task.Factory.StartNew((Action)this.ProcessBackgroundOperations, TaskCreationOptions.LongRunning);
            if (this.backgroundOperations.Count > 0)
            {
                this.wakeUpThread.Set();
            }
        }

        public void Enqueue(TBackgroundOperation backgroundOperation)
        {
            backgroundOperation.Id = this.GetNextOperationId();
            this.persistence[backgroundOperation.Id] = backgroundOperation;
            this.persistence.Flush();

            if (!this.isStopping)
            {
                this.backgroundOperations.Enqueue(backgroundOperation);
                this.wakeUpThread.Set();
            }
        }

        public void Shutdown()
        {
            this.isStopping = true;
            this.wakeUpThread.Set();
            this.backgroundThread.Wait();
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (this.persistence != null)
            {
                this.persistence.Dispose();
                this.persistence = null;
            }

            if (this.backgroundThread != null)
            {
                this.backgroundThread.Dispose();
                this.backgroundThread = null;
            }
        }

        private long GetNextOperationId()
        {
            return Interlocked.Increment(ref this.lastOperationId);
        }

        private void EnqueueSavedOperations()
        {
            foreach (long operationId in this.persistence.Keys)
            {
                TBackgroundOperation backgroundOperation = this.persistence[operationId];
                if (backgroundOperation.Id > this.lastOperationId)
                {
                    this.lastOperationId = backgroundOperation.Id;
                }

                this.backgroundOperations.Enqueue(backgroundOperation);                
            }
        }

        private AcquireGVFSLockResult WaitToAcquireGVFSLock()
        {
            while (!this.context.Repository.GVFSLock.TryAcquireLock())
            {
                if (this.isStopping)
                {
                    return AcquireGVFSLockResult.ShuttingDown;
                }

                Thread.Sleep(ActionRetryDelayMS);
            }

            return AcquireGVFSLockResult.LockAcquired;
        }
     
        private void ProcessBackgroundOperations()
        {
            TBackgroundOperation backgroundOperation;

            while (true)
            {
                AcquireGVFSLockResult acquireLockResult = AcquireGVFSLockResult.ShuttingDown;

                try
                {
                    this.wakeUpThread.WaitOne();

                    if (this.isStopping)
                    {
                        return;
                    }

                    acquireLockResult = this.WaitToAcquireGVFSLock();
                    switch (acquireLockResult)
                    {
                        case AcquireGVFSLockResult.LockAcquired:
                            break;
                        case AcquireGVFSLockResult.ShuttingDown:
                            return;
                        default:
                            this.LogErrorAndExit("Invalid " + nameof(AcquireGVFSLockResult) + " result");
                            return;
                    }

                    this.RunCallbackUntilSuccess(this.preCallback, "PreCallback");

                    int tasksProcessed = 0;
                    while (this.backgroundOperations.TryPeek(out backgroundOperation))
                    {
                        if (tasksProcessed % LogUpdateTaskThreshold == 0 && 
                            (tasksProcessed >= LogUpdateTaskThreshold || this.backgroundOperations.Count >= LogUpdateTaskThreshold))
                        {
                            this.LogTaskProcessingStatus(tasksProcessed);
                        }

                        if (this.isStopping)
                        {
                            // If we are stopping, then GVFlt has already been shut down
                            // Some of the queued background tasks may require GVFlt, and so it is unsafe to
                            // proceed.  GVFS will resume any queued tasks next time it is mounted
                            this.persistence.Flush();
                            return;
                        }
                        
                        CallbackResult callbackResult = this.callback(backgroundOperation);
                        switch (callbackResult)
                        {
                            case CallbackResult.Success:                                
                                this.backgroundOperations.TryDequeue(out backgroundOperation);
                                this.persistence.Remove(backgroundOperation.Id);                                
                                ++tasksProcessed;
                                break;

                            case CallbackResult.RetryableError:
                                if (!this.isStopping)
                                {
                                    Thread.Sleep(ActionRetryDelayMS);
                                }

                                break;

                            case CallbackResult.FatalError:
                                this.LogErrorAndExit("Callback encountered fatal error, exiting process");
                                break;

                            default:
                                this.LogErrorAndExit("Invalid background operation result");
                                break;
                        }
                    }

                    this.persistence.Flush();

                    if (tasksProcessed >= LogUpdateTaskThreshold)
                    {
                        EventMetadata metadata = new EventMetadata();
                        metadata.Add("Area", EtwArea);
                        metadata.Add("TasksProcessed", tasksProcessed);
                        metadata.Add("Message", "Processing background tasks complete");
                        this.context.Tracer.RelatedEvent(EventLevel.Informational, "TaskProcessingStatus", metadata);
                    }

                    if (this.isStopping)
                    {
                        return;
                    }
                }
                catch (Exception e)
                {
                    this.LogErrorAndExit("ProcessBackgroundOperations caught unhandled exception, exiting process", e);
                }
                finally
                {
                    if (acquireLockResult == AcquireGVFSLockResult.LockAcquired)
                    {
                        this.RunCallbackUntilSuccess(this.postCallback, "PostCallback");
                        if (this.backgroundOperations.IsEmpty)
                        {
                            this.context.Repository.GVFSLock.ReleaseLock();
                        }
                    }
                }
            }
        }

        private void RunCallbackUntilSuccess(Func<CallbackResult> callback, string errorHeader)
        {
            while (true)
            {
                CallbackResult callbackResult = callback();
                switch (callbackResult)
                {
                    case CallbackResult.Success:
                        return;

                    case CallbackResult.RetryableError:
                        if (this.isStopping)
                        {
                            return;
                        }

                        Thread.Sleep(ActionRetryDelayMS);
                        break;

                    case CallbackResult.FatalError:
                        this.LogErrorAndExit(errorHeader + " encountered fatal error, exiting process");
                        return;

                    default:
                        this.LogErrorAndExit(errorHeader + " result could not be found");
                        return;
                }
            }
        }

        private void LogWarning(string message)
        {
            EventMetadata metadata = new EventMetadata();
            metadata.Add("Area", EtwArea);
            metadata.Add("Message", message);
            this.context.Tracer.RelatedEvent(EventLevel.Warning, "Warning", metadata);
        }

        private void LogError(string message, Exception e = null)
        {
            this.LogError(message, e, exit: false);
        }

        private void LogErrorAndExit(string message, Exception e = null)
        {
            this.LogError(message, e, exit: true);
        }

        private void LogError(string message, Exception e, bool exit)
        {
            EventMetadata metadata = new EventMetadata();
            metadata.Add("Area", EtwArea);
            if (e != null)
            {
                metadata.Add("Exception", e.ToString());
            }

            metadata.Add("ErrorMessage", message);
            this.context.Tracer.RelatedError(metadata);
            if (exit)
            {
                Environment.Exit(1);
            }
        }

        private void LogTaskProcessingStatus(int tasksProcessed)
        {
            EventMetadata metadata = new EventMetadata();
            metadata.Add("BackgroundOperations", EtwArea);
            metadata.Add("TasksProcessed", tasksProcessed);
            metadata.Add("TasksRemaining", this.backgroundOperations.Count);
            this.context.Tracer.RelatedEvent(EventLevel.Informational, "TaskProcessingStatus", metadata);
        }
    }
}
