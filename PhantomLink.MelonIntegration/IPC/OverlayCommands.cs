using PhantomLink.MelonIntegration.Features;

namespace PhantomLink.MelonIntegration.IPC
{
    public static class OverlayCommands
    {
        public static void Register()
        {
            IpcRouter.RegisterHandler("TOGGLE_FPS_DISPLAY", OverlayManager.ToggleFPS);
            IpcRouter.RegisterHandler("TOGGLE_COLLIDERS", OverlayManager.ToggleColliders);
        }
    }
}
