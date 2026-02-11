using PhantomLink.MelonIntegration.Features;

namespace PhantomLink.MelonIntegration.IPC
{
    public static class CameraCommands
    {
        public static void Register()
        {
            IpcRouter.RegisterHandler("TOGGLE_FREECAM", CameraManager.ToggleFreeCam);
            IpcRouter.RegisterHandler("TOGGLE_FULLBRIGHT", CameraManager.ToggleFullBright);
            IpcRouter.RegisterHandler("TELEPORT_TO_CAMERA", CameraManager.TeleportPlayerToCamera);
        }
    }
}
