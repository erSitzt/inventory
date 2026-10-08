using System.ServiceProcess;
using System.Threading;

namespace Inventory.Runner
{
    internal sealed class AgentService : ServiceBase
    {
        private readonly AgentLoop _loop = new AgentLoop();

        public AgentService()
        {
            ServiceName = Inventory.Contracts.AgentProtocol.ServiceName;
            CanStop = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args) =>
            new Thread(_loop.Run) { IsBackground = true, Name = "AgentLoop" }.Start();

        protected override void OnStop() => _loop.Stop();
    }
}
