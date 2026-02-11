using PhantomLink.MelonIntegration.Core;
using PhantomLink.MelonIntegration.Features;

namespace PhantomLink.MelonIntegration.IPC
{
    public static class SceneCommands
    {
        public static void Register()
        {
            IpcRouter.RegisterHandler("START_SCENE_SYNC", SceneSyncManager.StartSync);
            IpcRouter.RegisterHandler("STOP_SCENE_SYNC", SceneSyncManager.StopSync);
            IpcRouter.RegisterHandler("POLL_SCENE_SYNC", SceneSyncManager.PollSync);
            IpcRouter.RegisterHandler("SCENE_INFO", GetSceneInfo);
            IpcRouter.RegisterHandler("OBJECT_INFO", GetObjectInfo);
            IpcRouter.RegisterHandler("GET_MESH", SceneSyncManager.GetMeshInfo);
        }

        private static string GetSceneInfo(string args, string[] parts)
        {
            return SceneSyncManager.GetSceneInfo(args, parts);
        }

        private static string GetObjectInfo(string args, string[] parts)
        {
            if (parts.Length < 2) return "ERROR|Missing object ID";
            return SceneSyncManager.GetObjectInfo(parts[1]);
        }
    }
}
