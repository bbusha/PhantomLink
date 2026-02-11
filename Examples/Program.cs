using System;
using PhantomLink.Core;

namespace Examples
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Unity Runtime Mod Tool - Example Usage");
            Console.WriteLine("======================================");

            Console.WriteLine("Starting IPC client...");
            IPCMeloaderClient.Start();

            Console.WriteLine("Sending PING...");
            var response = IPCMeloaderClient.SendCommandAsync("PING").GetAwaiter().GetResult();
            Console.WriteLine($"Response: {response}");

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();

            IPCMeloaderClient.Stop();
        }
    }
}
