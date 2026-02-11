using System.Reflection;

namespace PhantomLink.MelonIntegration
{
    internal static class ReflectionCompat
    {
        public static object GetValueCompat(this PropertyInfo property, object obj)
        {
            if (property == null)
                return null;
#if NET35
            return property.GetValue(obj, null);
#else
            return property.GetValue(obj);
#endif
        }

        public static void SetValueCompat(this PropertyInfo property, object obj, object value)
        {
            if (property == null)
                return;
#if NET35
            property.SetValue(obj, value, null);
#else
            property.SetValue(obj, value);
#endif
        }
    }
}

