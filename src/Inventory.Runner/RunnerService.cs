using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using Inventory.Contracts;

namespace Inventory.Runner
{
    /// <summary>
    /// Hülle für den Start über den Service Control Manager: führt genau einen Job aus und beendet sich.
    /// Der Job steht entweder in der Befehlszeile des Dienstes oder wird beim Start als Argument übergeben.
    /// </summary>
    internal sealed class RunnerService : ServiceBase
    {
        private const int ErrorInvalidParameter = 87;
        private readonly Func<string, string>? _resolveJobDir;
        private JobExecutor? _executor;

        public RunnerService(string jobDir)
        {
            _executor = new JobExecutor(jobDir);
            CanStop = true;
            AutoLog = false;
        }

        /// <summary>Dienst ohne festen Job: die Job-ID kommt mit dem Startaufruf.</summary>
        public RunnerService(Func<string, string> resolveJobDir)
        {
            _resolveJobDir = resolveJobDir;
            CanStop = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            if (_executor == null)
            {
                // Nur eine Job-ID, kein Pfad: der Dienst läuft als SYSTEM und soll nichts außerhalb des Job-Ordners anfassen.
                var jobId = args.LastOrDefault(JobFiles.IsJobId);
                if (jobId == null)
                {
                    ExitCode = ErrorInvalidParameter;
                    throw new ArgumentException("Job id missing or invalid.");
                }
                _executor = new JobExecutor(_resolveJobDir!(jobId));
            }
            var executor = _executor;
            var worker = new Thread(() =>
            {
                executor.Run();
                Stop();
            })
            { IsBackground = true, Name = "JobExecutor" };
            worker.Start();
        }

        protected override void OnStop() => _executor?.Cancel();
    }
}
