using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace varManager.Ui
{
    /// <summary>
    /// Keeps one removal of old temporary links waiting. A newer load replaces it, because the newer load's list
    /// already holds every old link except the ones it needs, and removing those would break that load.
    /// </summary>
    public sealed class TempLinkRemoval
    {
        private CancellationTokenSource? pending;

        public Task Schedule(string vamPath, IReadOnlyList<string> fileNames, TimeSpan pause)
        {
            var next = new CancellationTokenSource();
            Interlocked.Exchange(ref pending, next)?.Cancel();
            return VamBridge.RemoveTempLinksAfterLoad(vamPath, fileNames, pause, next.Token);
        }
    }
}
