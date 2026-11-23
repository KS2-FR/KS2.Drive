using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace KS2Drive.Tests.UI
{
    /// <summary>
    /// Runs test code on a new STA thread (required by WPF objects) and rethrows its exception, assertion failures included.
    /// </summary>
    internal static class StaThread
    {
        public static void Run(Action Action, int TimeoutMilliseconds = 30000)
        {
            ExceptionDispatchInfo Error = null;
            var T = new Thread(() =>
            {
                try
                {
                    Action();
                }
                catch (Exception e)
                {
                    Error = ExceptionDispatchInfo.Capture(e);
                }
            });
            T.SetApartmentState(ApartmentState.STA);
            T.IsBackground = true;
            T.Start();
            if (!T.Join(TimeoutMilliseconds)) throw new TimeoutException("The STA test action did not complete in time");
            Error?.Throw();
        }
    }
}
