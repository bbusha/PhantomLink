using PhantomLink.MelonIntegration.Features;

namespace PhantomLink.MelonIntegration.IPC
{
    public static class GameCommands
    {
        public static void Register()
        {
            IpcRouter.RegisterHandler("SET_TIMESCALE", TimeManager.SetTimeScale);
            IpcRouter.RegisterHandler("FREEZE_TIME", TimeManager.FreezeTime);
        }
    }
}
