using PhantomLink.MelonIntegration.Features;

namespace PhantomLink.MelonIntegration.IPC
{
    public static class CheatCommands
    {
        public static void Register()
        {
            IpcRouter.RegisterHandler("SET_CHEAT", SetCheat);
        }

        private static string SetCheat(string args, string[] parts)
        {
            return CheatManager.SetCheat(args, parts);
        }
    }
}
