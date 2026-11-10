using System;
using Inventory.Agent;
using Inventory.Contracts;

namespace Inventory.Runner
{
    /// <summary>Der Agent unter Windows: Jobs führt derselbe <see cref="JobExecutor"/> aus wie im agentenlosen Betrieb.</summary>
    internal sealed class WindowsAgentPlatform : IAgentPlatform
    {
        public string Directory => RunnerInfo.Directory;

        public string Version => RunnerInfo.Version;

        public string Runtime => RunnerInfo.Runtime;

        public string HostName => Environment.MachineName;

        /// <summary>Rechnername mit primärem DNS-Suffix, ohne Namensauflösung; null, wenn der Rechner kein Suffix hat.</summary>
        public string? DnsName()
        {
            try
            {
                var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                return string.IsNullOrWhiteSpace(properties.DomainName) ? null : properties.HostName + "." + properties.DomainName;
            }
            catch (System.Net.NetworkInformation.NetworkInformationException)
            {
                return null;
            }
        }

        public IAgentJob CreateJob(string jobDir) => new Job(new JobExecutor(jobDir));

        public DateTime BootTimeUtc() => JobExecutor.BootTimeUtc();

        public void PrepareUpdatedFile(string path)
        {
        }

        private sealed class Job : IAgentJob
        {
            private readonly JobExecutor _executor;

            public Job(JobExecutor executor) => _executor = executor;

            public JobResult Run() => _executor.Run();

            public void Cancel() => _executor.Cancel();
        }
    }
}
