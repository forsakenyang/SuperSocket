﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using SuperSocket.Common;
using SuperSocket.SocketBase;
using SuperSocket.SocketBase.Config;
using SuperSocket.SocketBase.Logging;
using SuperSocket.SocketBase.Metadata;

namespace SuperSocket.SocketEngine
{
    class PerformanceMonitor : IDisposable
    {
        private Timer m_PerformanceTimer;
        private int m_TimerInterval;
        private ILog m_PerfLog;

        private PerformanceCounter m_CpuUsagePC;
        private PerformanceCounter m_ThreadCountPC;
        private PerformanceCounter m_WorkingSetPC;

        private int m_CpuCores = 1;

        private IWorkItem[] m_AppServers;

        private IWorkItem m_ServerManager;

        private List<KeyValuePair<string, StatusInfoAttribute[]>> m_ServerStatusMetadataSource;

        public PerformanceMonitor(IRootConfig config, IEnumerable<IWorkItem> appServers, IWorkItem serverManager, ILogFactory logFactory)
        {
            m_PerfLog = logFactory.GetLog("Performance");

            m_AppServers = appServers.ToArray();

            m_ServerManager = serverManager;

            Process process = Process.GetCurrentProcess();

            m_CpuCores = Environment.ProcessorCount;

            var isUnix = Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX;
            var instanceName = (isUnix || Platform.IsMono) ? string.Format("{0}/{1}", process.Id, process.ProcessName) : GetPerformanceCounterInstanceName(process);

            SetupPerformanceCounters(instanceName);

            m_TimerInterval = config.PerformanceDataCollectInterval * 1000;
            m_PerformanceTimer = new Timer(OnPerformanceTimerCallback);
        }

        private void SetupServerStatusMetadata()
        {
            m_ServerStatusMetadataSource = new List<KeyValuePair<string, StatusInfoAttribute[]>>(m_AppServers.Length + 1);

            m_ServerStatusMetadataSource.Add(
                new KeyValuePair<string, StatusInfoAttribute[]>(string.Empty, 
                    new StatusInfoAttribute[]
                    {
                        new StatusInfoAttribute("CpuUsage") { Name = "CPU Usage", Format = "{0:0.00}%", Order = 0 },
                        new StatusInfoAttribute("WorkingSet") { Name = "Physical Memory Usage", Format = "{0:N}", Order = 1 },
                        new StatusInfoAttribute("TotalThreadCount") { Name = "Total Thread Count", Order = 2 },
                        new StatusInfoAttribute("AvailableWorkingThreads") { Name = "Available Working Threads", Order = 3 },
                        new StatusInfoAttribute("AvailableCompletionPortThreads") { Name = "Available Completion Port Threads", Order = 4 },
                        new StatusInfoAttribute("MaxWorkingThreads") { Name = "Maximum Working Threads", Order = 5 },
                        new StatusInfoAttribute("MaxCompletionPortThreads") { Name = "Maximum Completion Port Threads", Order = 6 }
                    }));

            for (var i = 0; i < m_AppServers.Length; i++)
            {
                var server = m_AppServers[i];
                m_ServerStatusMetadataSource.Add(
                    new KeyValuePair<string, StatusInfoAttribute[]>(server.Name, server.GetServerStatusMetadata()));
            }

            if (m_ServerManager != null && m_ServerManager.State == ServerState.Running)
            {
                m_ServerManager.TransferSystemMessage("ServerMetadataCollected", m_ServerStatusMetadataSource);
            }
        }

        private void SetupPerformanceCounters(string instanceName)
        {
            m_CpuUsagePC = new PerformanceCounter("Process", "% Processor Time", instanceName);
            m_ThreadCountPC = new PerformanceCounter("Process", "Thread Count", instanceName);
            m_WorkingSetPC = new PerformanceCounter("Process", "Working Set", instanceName);
        }

        //Tt is only used in windows
        private static string GetPerformanceCounterInstanceName(Process process)
        {
            var processId = process.Id;
            var processCategory = new PerformanceCounterCategory("Process");
            var runnedInstances = processCategory.GetInstanceNames();

            foreach (string runnedInstance in runnedInstances)
            {
                if (!runnedInstance.StartsWith(process.ProcessName, StringComparison.OrdinalIgnoreCase))
                    continue;

                using (var performanceCounter = new PerformanceCounter("Process", "ID Process", runnedInstance, true))
                {
                    if ((int)performanceCounter.RawValue == processId)
                    {
                        return runnedInstance;
                    }
                }
            }

            return process.ProcessName;
        }

        public void Start()
        {
            SetupServerStatusMetadata();
            m_PerformanceTimer.Change(0, m_TimerInterval);
        }

        public void Stop()
        {
            m_PerformanceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void OnPerformanceTimerCallback(object state)
        {
            var nodeStatus = new NodeStatus();

            int availableWorkingThreads, availableCompletionPortThreads;
            ThreadPool.GetAvailableThreads(out availableWorkingThreads, out availableCompletionPortThreads);

            int maxWorkingThreads;
            int maxCompletionPortThreads;
            ThreadPool.GetMaxThreads(out maxWorkingThreads, out maxCompletionPortThreads);

            StatusInfoCollection bootstrapStatus = null;

            var retry = false;

            while(true)
            {
                try
                {
                    bootstrapStatus = new StatusInfoCollection();

                    bootstrapStatus[StatusInfoKeys.AvailableWorkingThreads] = availableWorkingThreads;
                    bootstrapStatus[StatusInfoKeys.AvailableCompletionPortThreads] = availableCompletionPortThreads;
                    bootstrapStatus[StatusInfoKeys.MaxCompletionPortThreads] = maxCompletionPortThreads;
                    bootstrapStatus[StatusInfoKeys.MaxWorkingThreads] = maxWorkingThreads;
                    bootstrapStatus[StatusInfoKeys.TotalThreadCount] = (int)m_ThreadCountPC.NextValue();
                    bootstrapStatus[StatusInfoKeys.CpuUsage] = m_CpuUsagePC.NextValue() / m_CpuCores;
                    bootstrapStatus[StatusInfoKeys.WorkingSet] = (long)m_WorkingSetPC.NextValue();

                    nodeStatus.BootstrapStatus = bootstrapStatus;
                    break;
                }
                catch (InvalidOperationException e)
                {
                    //Only re-get performance counter one time
                    if (retry)
                        throw e;

                    //Only re-get performance counter for .NET/Windows
                    if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX || Platform.IsMono)
                        throw e;

                    //If a same name process exited, this process's performance counters instance name could be changed,
                    //so if the old performance counter cannot be access, get the performance counter's name again
                    var newInstanceName = GetPerformanceCounterInstanceName(Process.GetCurrentProcess());
                    SetupPerformanceCounters(newInstanceName);
                    retry = true;
                }
            }

            var instancesStatus = new List<StatusInfoCollection>(m_AppServers.Length);

            var perfBuilder = new StringBuilder();

            perfBuilder.AppendLine("---------------------------------------------------");
            perfBuilder.AppendLine(string.Format("CPU Usage: {0:0.00}%, Physical Memory Usage: {1:N}, Total Thread Count: {2}", bootstrapStatus[StatusInfoKeys.CpuUsage], bootstrapStatus[StatusInfoKeys.WorkingSet], bootstrapStatus[StatusInfoKeys.TotalThreadCount]));
            perfBuilder.AppendLine(string.Format("AvailableWorkingThreads: {0}, AvailableCompletionPortThreads: {1}", bootstrapStatus[StatusInfoKeys.AvailableWorkingThreads], bootstrapStatus[StatusInfoKeys.AvailableCompletionPortThreads]));
            perfBuilder.AppendLine(string.Format("MaxWorkingThreads: {0}, MaxCompletionPortThreads: {1}", bootstrapStatus[StatusInfoKeys.MaxWorkingThreads], bootstrapStatus[StatusInfoKeys.MaxCompletionPortThreads]));

            for (var i = 0; i < m_AppServers.Length; i++)
            {
                var s = m_AppServers[i];

                var metadata = m_ServerStatusMetadataSource[i + 1].Value;

                if (metadata == null)
                {
                    perfBuilder.AppendLine(string.Format("{0} ----------------------------------", s.Name));
                    perfBuilder.AppendLine(string.Format("{0}: {1}", "IsRunning", s.State == ServerState.Running));
                }
                else
                {
                    var serverStatus = s.CollectServerStatus(bootstrapStatus);

                    instancesStatus.Add(serverStatus);

                    perfBuilder.AppendLine(string.Format("{0} ----------------------------------", serverStatus.Tag));

                    for (var j = 0; j < metadata.Length; j++)
                    {
                        var statusInfoAtt = metadata[j];

                        if (!statusInfoAtt.OutputInPerfLog)
                            continue;

                        var statusValue = serverStatus[statusInfoAtt.Key];

                        if (statusValue == null)
                            continue;

                        perfBuilder.AppendLine(
                            string.Format("{0}: {1}", statusInfoAtt.Name,
                            string.IsNullOrEmpty(statusInfoAtt.Format) ? statusValue : string.Format(statusInfoAtt.Format, statusValue)));
                    }
                }
            }

            m_PerfLog.Info(perfBuilder.ToString());

            nodeStatus.InstancesStatus = instancesStatus.ToArray();

            if (m_ServerManager != null && m_ServerManager.State == ServerState.Running)
            {
                m_ServerManager.TransferSystemMessage("ServerStatusCollected", nodeStatus);
            }
        }

        public void Dispose()
        {
            if (m_PerformanceTimer != null)
            {
                m_PerformanceTimer.Dispose();
                m_PerformanceTimer = null;
            }

            if (m_CpuUsagePC != null)
            {
                m_CpuUsagePC.Close();
                m_CpuUsagePC = null;
            }

            if (m_ThreadCountPC != null)
            {
                m_ThreadCountPC.Close();
                m_ThreadCountPC = null;
            }

            if (m_WorkingSetPC != null)
            {
                m_WorkingSetPC.Close();
                m_WorkingSetPC = null;
            }
        }
    }
}
