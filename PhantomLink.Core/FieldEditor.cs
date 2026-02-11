using System;
using System.Collections.Generic;
using System.Reflection;

namespace PhantomLink.Core
{


    public static class FieldEditor
    {
        public static IEnumerable<FieldInfoWrapper> GetFields(object target)
        {
            if (target == null)
                yield break;

            var type = target.GetType();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                FieldInfoWrapper wrapper = null;
                try
                {
                    var value = field.GetValue(target);
                    wrapper = new FieldInfoWrapper
                    {
                        FieldName = field.Name,
                        DeclaringType = field.DeclaringType?.FullName,
                        FieldType = field.FieldType.Name,
                        IsStatic = field.IsStatic,
                        IsPublic = field.IsPublic,
                        IsLiteral = field.IsLiteral,
                        IsInitOnly = field.IsInitOnly,
                        CanWrite = !field.IsInitOnly && !field.IsLiteral,
                        FieldInfo = field,
                        Value = value
                    };
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Failed to get field {field.Name}: {ex.Message}");
                }
                
                if (wrapper != null)
                    yield return wrapper;
            }
        }

        public static bool SetFieldValue(object target, string fieldName, object value)
        {
            if (target == null)
                return false;

            try
            {
                var field = target.GetType().GetField(fieldName, 
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (field == null)
                {
                    Logger.LogWarning($"Field {fieldName} not found");
                    return false;
                }

                if (!field.CanWrite())
                {
                    Logger.LogWarning($"Field {fieldName} is read-only");
                    return false;
                }

                var convertedValue = ConvertValue(value, field.FieldType);
                field.SetValue(target, convertedValue);
                Logger.LogInfo($"Field {fieldName} set to {convertedValue}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to set field {fieldName}: {ex.Message}");
                return false;
            }
        }

        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            if (targetType.IsInstanceOfType(value))
                return value;

            try
            {
                return Convert.ChangeType(value, targetType);
            }
            catch
            {
                Logger.LogWarning($"Failed to convert value {value} to type {targetType.Name}");
                return value;
            }
        }

        private static bool CanWrite(this FieldInfo field)
        {
            return !field.IsInitOnly && !field.IsLiteral;
        }
    }
}
