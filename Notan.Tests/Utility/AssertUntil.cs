using System;
using System.Diagnostics;

namespace Notan.Tests.Utility;

internal static class AssertUntil
{
    private const int defaultTimeout = 100;

    public static void True(int timeoutMilliseconds, Func<bool> pred)
    {
        var watch = Stopwatch.StartNew();
        while (!pred())
        {
            if (watch.ElapsedMilliseconds > timeoutMilliseconds)
            {
                throw new TimeoutException();
            }
        }
    }

    public static void True(Func<bool> pred) => True(defaultTimeout, pred);

    public static void Throw(int timeoutMilliseconds, Action action)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                action();
            }
            catch
            {
                return;
            }

            if (watch.ElapsedMilliseconds > timeoutMilliseconds)
            {
                throw new TimeoutException();
            }
        }
    }

    public static void Throw(Action action) => Throw(defaultTimeout, action);
}
