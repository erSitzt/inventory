using System.ServiceProcess;
using System.Threading;

namespace Inventory.Runner
{
    /// <summary>Hülle für den Start über den Service Control Manager: führt genau einen Job aus und beendet sich.</summary>
    internal sealed class RunnerService : ServiceBase
    {
        private readonly JobExecutor _executor;

        public RunnerService(string jobDir)
        {
            _executor = new JobExecutor(jobDir);
            CanStop = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            var worker = new Thread(() =>
            {
                _executor.Run();
                Stop();
            })
            { IsBackground = true, Name = "JobExecutor" };
            worker.Start();
        }

        protected override void OnStop() => _executor.Cancel();
    }
}
