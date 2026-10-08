using System;
using System.Reflection;
using Inventory.Contracts;

namespace Inventory.Runner
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == RunnerModes.Version)
            {
                Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version);
                return 0;
            }

            Console.Error.WriteLine("Aufruf: InventoryRunner (--service|--console) <jobdir> | --version");
            return 2;
        }
    }
}
