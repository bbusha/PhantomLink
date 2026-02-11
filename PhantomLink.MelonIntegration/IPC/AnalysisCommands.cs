using System.Text;
using PhantomLink.MelonIntegration.Features;

namespace PhantomLink.MelonIntegration.IPC
{
    public static class AnalysisCommands
    {
        public static void Register()
        {
            IpcRouter.RegisterHandler("START_ANALYSIS", StartAnalysis);
            IpcRouter.RegisterHandler("STOP_ANALYSIS", StopAnalysis);
            IpcRouter.RegisterHandler("GET_INFERRED_ROLES", GetInferredRoles);
        }

        private static string StartAnalysis(string args, string[] parts)
        {
            BehaviorAnalyzer.Start();
            return "SUCCESS|Analysis started";
        }

        private static string StopAnalysis(string args, string[] parts)
        {
            BehaviorAnalyzer.Stop();
            return "SUCCESS|Analysis stopped";
        }

        private static string GetInferredRoles(string args, string[] parts)
        {
            var roles = BehaviorAnalyzer.GetInferredRoles();
            if (string.IsNullOrEmpty(roles)) return "ROLES|None";
            return $"ROLES|{roles}";
        }
    }
}