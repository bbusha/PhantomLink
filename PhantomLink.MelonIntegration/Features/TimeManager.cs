using System;
using System.Reflection;
using PhantomLink.MelonIntegration.Core;

namespace PhantomLink.MelonIntegration.Features
{
    public static class TimeManager
    {
        private static float _timeScaleBeforePause = 1f;
        private static bool _timeFrozen = false;

        public static string SetTimeScale(string args, string[] parts)
        {
            if (parts.Length < 2) return "ERROR|Invalid format";
            if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float scale))
                return "ERROR|Invalid scale";

            var time = ReflectionHelper.FindType("UnityEngine.Time");
            var scaleProp = ReflectionHelper.GetProperty(time, "timeScale");
            if (scaleProp != null)
            {
                scaleProp.SetValue(null, scale, null);
                return "SUCCESS|OK";
            }
            return "ERROR|Time.timeScale not found";
        }

        public static string FreezeTime(string args, string[] parts)
        {
            var time = ReflectionHelper.FindType("UnityEngine.Time");
            var scaleProp = ReflectionHelper.GetProperty(time, "timeScale");
            if (scaleProp == null) return "ERROR|Time.timeScale not found";

            if (!_timeFrozen)
            {
                _timeScaleBeforePause = Convert.ToSingle(scaleProp.GetValue(null, null));
                scaleProp.SetValue(null, 0f, null);
                _timeFrozen = true;
            }
            else
            {
                scaleProp.SetValue(null, _timeScaleBeforePause, null);
                _timeFrozen = false;
            }
            return $"SUCCESS|FROZEN|{_timeFrozen}";
        }
    }
}
