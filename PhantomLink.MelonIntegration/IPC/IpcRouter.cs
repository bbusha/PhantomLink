using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using PhantomLink.MelonIntegration.Core;

namespace PhantomLink.MelonIntegration.IPC
{
    public static class IpcRouter
    {
        private static readonly Dictionary<string, Func<string, string[], string>> _handlers = new Dictionary<string, Func<string, string[], string>>(StringComparer.OrdinalIgnoreCase);

        public static void RegisterHandler(string command, Func<string, string[], string> handler)
        {
            _handlers[command] = handler;
        }

        public static bool TryProcess(string commandLine, out string result)
        {
            result = null;
            try
            {
                if (string.IsNullOrEmpty(commandLine)) 
                {
                    result = "ERROR|Empty command";
                    return true;
                }

                string[] parts = null;
                string cmdType;
                string args = null;

                if (commandLine.Contains("|"))
                {
                    parts = commandLine.Split('|');
                    if (parts.Length == 0)
                    {
                        result = "ERROR|Invalid command format";
                        return true;
                    }
                    cmdType = parts[0].Trim().ToUpperInvariant();
                    if (parts.Length > 1) args = string.Join("|", parts.Skip(1).ToArray());
                }
                else
                {
                    var trimmed = commandLine.Trim();
                    var spaceIndex = trimmed.IndexOf(' ');
                    if (spaceIndex < 0)
                    {
                        cmdType = trimmed.ToUpperInvariant();
                    }
                    else
                    {
                        cmdType = trimmed.Substring(0, spaceIndex).ToUpperInvariant();
                        args = trimmed.Substring(spaceIndex + 1).Trim();
                        parts = new[] { cmdType, args };
                    }
                }

                if (_handlers.TryGetValue(cmdType, out var handler))
                {
                    result = handler(args, parts);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Error($"Error processing command '{commandLine}': {ex}");
                result = $"ERROR|{ex.Message}";
                return true;
            }
        }

        public static string Process(string commandLine)
        {
            if (TryProcess(commandLine, out var result))
                return result;
            return $"ERROR|Unknown command: {commandLine.Split('|')[0]}";
        }
    }
}
