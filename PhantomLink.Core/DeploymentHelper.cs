using System;
using System.Collections.Generic;
using System.IO;

namespace PhantomLink.Core
{
    public class DeploymentResult
    {
        public bool Success { get; set; }
        public List<string> DeployedFiles { get; set; } = new List<string>();
        public string ErrorMessage { get; set; }
    }

    public static class DeploymentHelper
    {
        public static DeploymentResult DeployToGameDirectory(string gameDirectory)
        {
            var result = new DeploymentResult();
            
            try
            {
                if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory))
                {
                    result.Success = false;
                    result.ErrorMessage = "Invalid game directory";
                    return result;
                }

                // Get current assembly location
                var currentAssembly = System.Reflection.Assembly.GetExecutingAssembly();
                var currentDirectory = Path.GetDirectoryName(currentAssembly.Location);
                
                if (string.IsNullOrEmpty(currentDirectory))
                {
                    result.Success = false;
                    result.ErrorMessage = "Could not determine current assembly location";
                    return result;
                }

                var deployFolder = Path.Combine(gameDirectory, "PhantomLink");
                Directory.CreateDirectory(deployFolder);

                foreach (var filePath in Directory.EnumerateFiles(currentDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    var file = Path.GetFileName(filePath);
                    var ext = Path.GetExtension(file)?.ToLowerInvariant() ?? "";
                    if (ext != ".exe" && ext != ".dll" && ext != ".pdb" && ext != ".json" && ext != ".config")
                        continue;

                    var destPath = Path.Combine(deployFolder, file);
                    File.Copy(filePath, destPath, true);
                    result.DeployedFiles.Add(file);
                }

                var runtimesSrc = Path.Combine(currentDirectory, "runtimes");
                if (Directory.Exists(runtimesSrc))
                {
                    CopyDirectory(runtimesSrc, Path.Combine(deployFolder, "runtimes"), result.DeployedFiles);
                }

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir, List<string> deployed)
        {
            Directory.CreateDirectory(destDir);
            foreach (var filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = filePath.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var destPath = Path.Combine(destDir, relative);
                var destParent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destParent))
                    Directory.CreateDirectory(destParent);
                File.Copy(filePath, destPath, true);
                deployed.Add(Path.Combine("runtimes", relative));
            }
        }
    }
}
