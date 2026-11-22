using System;

namespace Celeste.Mod.CeilingUltra.Utils
{
    public delegate TReturn GetDelegate<TInstance, TReturn>(TInstance instance);

    internal static class FastReflection
    {
        public static GetDelegate<TInstance, TReturn> CreateGetDelegate<TInstance, TReturn>(Type type, string fieldName)
        {
            return CreateGetDelegateImpl<TInstance, TReturn>(type, fieldName);
        }

        private static GetDelegate<TInstance, TReturn> CreateGetDelegateImpl<TInstance, TReturn>(Type type, string name)
        {
            throw new PlatformNotSupportedException("The desktop Reflection.Emit body should have been replaced.");
        }
    }

    public sealed class GetterTarget
    {
        private readonly string hidden = "private-field";
        public string Read() => hidden;
    }
}
