﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

/// <summary>
/// Execute a given number of mutually independent build subprocesses represented by an array of
/// command lines with a given degree of parallelization.
/// </summary>
public sealed class ParallelRunner
{
    /// <summary>
    /// Helper class for launching mutually independent build subprocesses in parallel.
    /// It supports launching the processes and optionally redirecting their standard and
    /// error output streams to prevent them from interleaving in the final build output log.
    /// Multiple instances of this class representing the individual running processes
    /// can exist at the same time.
    /// </summmary>
    class ProcessSlot
    {
        /// <summary>
        /// Process slot index (used for diagnostic purposes)
        /// </summary>
        private readonly int _slotIndex;

        /// <summary>
        /// Event used to report that a process has exited
        /// </summary>
        private readonly AutoResetEvent _processExitEvent;

        /// <summary>
        /// Process object
        /// </summary>
        private ProcessRunner _processRunner;

        /// <summary>
        /// Diagnostic output log
        /// </summary>
        private StreamWriter _logWriter;

        /// <summary>
        /// Constructor stores global slot parameters and initializes the slot state machine
        /// </summary>
        /// <param name="slotIndex">Process slot index used for diagnostic purposes</param>
        /// <param name="processExitEvent">Event used to report process exit</param>
        public ProcessSlot(int slotIndex, AutoResetEvent processExitEvent, StreamWriter logWriter)
        {
            _slotIndex = slotIndex;
            _processExitEvent = processExitEvent;
            _logWriter = logWriter;
        }

        /// <summary>
        /// Launch a new process.
        /// </summary>
        /// <param name="processInfo">application to execute</param>
        /// <param name="jittedMethods">Jitted method collector</param>
        /// <param name="processIndex">Numeric index used to prefix messages pertaining to this process in the console output</param>
        /// <param name="processCount">Total number of processes being executed (used for displaying progress)</param>
        /// <param name="progressIndex">Number of processes that have already finished (for displaying progress)</param>
        public void Launch(ProcessInfo processInfo, ReadyToRunJittedMethods jittedMethods, int processIndex, int processCount, int progressIndex)
        {
            Debug.Assert(_processRunner == null);
            Console.WriteLine($"{processIndex} / {processCount} ({(progressIndex * 100 / processCount)}%): launching: {processInfo.ProcessPath} {processInfo.Arguments}");

            _processRunner = new ProcessRunner(processInfo, processIndex, processCount, jittedMethods, _processExitEvent);
        }

        public bool IsAvailable(ref int progressIndex)
        {
            if (_processRunner == null)
            {
                return true;
            }
            if (!_processRunner.IsAvailable(ref progressIndex))
            {
                return false;
            }
            _processRunner.Dispose();
            _processRunner = null;
            return true;
        }
    }

    /// <summary>
    /// Execute a given set of mutually independent build commands with the default
    /// degree of parallelism.
    /// </summary>
    /// <param name="processesToRun">Processes to execute in parallel</param>
    /// <param name="logWriter">Output log writer</param>
    public static void Run(IEnumerable<ProcessInfo> processesToRun, StreamWriter logWriter)
    {
        Run(processesToRun, logWriter, degreeOfParallelism: Environment.ProcessorCount);
    }

    /// <summary>
    /// Execute a given set of mutually independent build commands with given degree of
    /// parallelism.
    /// </summary>
    /// <param name="processesToRun">Processes to execute in parallel</param>
    /// <param name="logWriter">Diagnostic output stream</param>
    /// <param name="degreeOfParallelism">Maximum number of processes to execute in parallel</param>
    public static void Run(IEnumerable<ProcessInfo> processesToRun, StreamWriter logWriter, int degreeOfParallelism)
    {
        int processCount = processesToRun.Count();
        if (processCount < degreeOfParallelism)
        {
            // We never need a higher DOP than the number of process to execute
            degreeOfParallelism = processCount;
        }

        bool collectEtwTraces = processesToRun.Any(processInfo => processInfo.CollectJittedMethods);

        if (collectEtwTraces)
        {
            // In ETW collection mode, separate the processes to run into smaller batches as we need to keep
            // the process objects alive for the entire duration of the parallel execution, otherwise PID's
            // may get recycled by the OS and we can no longer back-translate PIDs in events to the logical
            // process executions. Without parallelization, we simply run the processes one by one.
            int etwCollectionBatching = (degreeOfParallelism == 1 ? 1 : 100);

            for (int batchStartIndex = 0; batchStartIndex < processCount; batchStartIndex += etwCollectionBatching)
            {
                BuildEtwProcesses(
                    startIndex: batchStartIndex,
                    processCount: processCount,
                    processesToRun.Skip(batchStartIndex).Take(etwCollectionBatching),
                    logWriter,
                    degreeOfParallelism);
            }
        }
        else
        {
            BuildProjects(startIndex: 0, processCount, processesToRun, null, logWriter, degreeOfParallelism);
        }
    }

    private static void BuildEtwProcesses(int startIndex, int processCount, IEnumerable<ProcessInfo> processesToRun, StreamWriter logWriter, int degreeOfParallelism)
    {
        using (TraceEventSession traceEventSession = new TraceEventSession("ReadyToRunTestSession"))
        {
            traceEventSession.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, (ulong)(ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.Loader));
            using (ReadyToRunJittedMethods jittedMethods = new ReadyToRunJittedMethods(traceEventSession, processesToRun))
            {
                Task.Run(() =>
                {
                    BuildProjects(startIndex, processCount, processesToRun, jittedMethods, logWriter, degreeOfParallelism);
                    traceEventSession.Stop();
                });
            }
            traceEventSession.Source.Process();
        }

        // Append jitted method info to the logs
        foreach (ProcessInfo processInfo in processesToRun)
        {
            if (processInfo.CollectJittedMethods)
            {
                using (StreamWriter processLogWriter = new StreamWriter(processInfo.LogPath, append: true))
                {
                    if (processInfo.JittedMethods != null)
                    {
                        processLogWriter.WriteLine($"Jitted methods ({processInfo.JittedMethods.Sum(moduleMethodsKvp => moduleMethodsKvp.Value.Count)} total):");
                        foreach (KeyValuePair<string, HashSet<string>> jittedMethodsPerModule in processInfo.JittedMethods)
                        {
                            foreach (string method in jittedMethodsPerModule.Value)
                            {
                                processLogWriter.WriteLine(jittedMethodsPerModule.Key + " -> " + method);
                            }
                        }
                    }
                    else
                    {
                        processLogWriter.WriteLine("Jitted method info not available");
                    }
                }
            }
        }
    }

    private static void BuildProjects(int startIndex, int processCount, IEnumerable<ProcessInfo> processesToRun, ReadyToRunJittedMethods jittedMethods, StreamWriter logWriter, int degreeOfParallelism)
    {
        using (AutoResetEvent processExitEvent = new AutoResetEvent(initialState: false))
        {
            ProcessSlot[] processSlots = new ProcessSlot[degreeOfParallelism];
            for (int index = 0; index < degreeOfParallelism; index++)
            {
                processSlots[index] = new ProcessSlot(index, processExitEvent, logWriter);
            }

            int progressIndex = startIndex;
            foreach (ProcessInfo processInfo in processesToRun)
            {
                // Allocate a process slot, potentially waiting on the exit event
                // when all slots are busy (running)
                ProcessSlot freeSlot = null;
                do
                {
                    foreach (ProcessSlot slot in processSlots)
                    {
                        if (slot.IsAvailable(ref progressIndex))
                        {
                            freeSlot = slot;
                            break;
                        }
                    }
                    if (freeSlot == null)
                    {
                        // All slots are busy - wait for a process to finish
                        processExitEvent.WaitOne();
                    }
                }
                while (freeSlot == null);

                freeSlot.Launch(processInfo, jittedMethods, startIndex++, processCount, progressIndex);
            }

            // We have launched all the commands, now wait for all processes to finish
            bool activeProcessesExist;
            do
            {
                activeProcessesExist = false;
                foreach (ProcessSlot slot in processSlots)
                {
                    if (!slot.IsAvailable(ref progressIndex))
                    {
                        activeProcessesExist = true;
                    }
                }
                if (activeProcessesExist)
                {
                    processExitEvent.WaitOne();
                }
            }
            while (activeProcessesExist);
        }
    }
}
