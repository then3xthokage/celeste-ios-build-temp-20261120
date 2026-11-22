using System;
using System.Threading;
using System.Threading.Tasks;

namespace TextCopy
{
    public static class ClipboardService
    {
        private static Func<string> getFunc;
        private static Action<string> setAction;
        private static Func<CancellationToken, Task<string>> getAsyncFunc;
        private static Func<string, CancellationToken, Task> setAsyncAction;

        static ClipboardService()
        {
            getFunc = CreateGet();
            setAction = CreateSet();
            getAsyncFunc = CreateAsyncGet();
            setAsyncAction = CreateAsyncSet();
        }

        public static string GetText() => getFunc();
        public static void SetText(string value) => setAction(value);
        public static Task<string> GetTextAsync(CancellationToken token) => getAsyncFunc(token);
        public static Task SetTextAsync(string value, CancellationToken token) => setAsyncAction(value, token);

        private static Func<string> CreateGet() => () => throw new PlatformNotSupportedException();
        private static Action<string> CreateSet() => _ => throw new PlatformNotSupportedException();
        private static Func<CancellationToken, Task<string>> CreateAsyncGet() =>
            _ => Task.FromException<string>(new PlatformNotSupportedException());
        private static Func<string, CancellationToken, Task> CreateAsyncSet() =>
            (_, __) => Task.FromException(new PlatformNotSupportedException());
    }
}
