using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace varManager.Ui
{
    /// <summary>
    /// Decodes small thumbnails off the UI thread and keeps only the most recent. The library has over
    /// 53,000 previews, so nothing here holds on to them all.
    /// </summary>
    public sealed class ThumbnailLoader
    {
        private const int DecodeWidth = 240;

        private readonly int capacity;
        private readonly SemaphoreSlim decoders = new SemaphoreSlim(2);
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>> cached =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<KeyValuePair<string, BitmapSource>> recent = new LinkedList<KeyValuePair<string, BitmapSource>>();

        public ThumbnailLoader(int capacity = 400)
        {
            this.capacity = capacity;
        }

        public int CachedCount
        {
            get
            {
                lock (cached)
                    return cached.Count;
            }
        }

        /// <summary>Calls back on a background thread with the picture, or null if it can't be read.</summary>
        public Task Load(string path, Action<BitmapSource?> done)
        {
            BitmapSource? hit = Find(path);
            if (hit != null)
            {
                done(hit);
                return Task.CompletedTask;
            }

            return Task.Run(async () =>
            {
                await decoders.WaitAsync().ConfigureAwait(false);
                try
                {
                    BitmapSource? image = Decode(path);
                    if (image != null)
                        Keep(path, image);
                    done(image);
                }
                finally
                {
                    decoders.Release();
                }
            });
        }

        private BitmapSource? Find(string path)
        {
            lock (cached)
            {
                if (!cached.TryGetValue(path, out LinkedListNode<KeyValuePair<string, BitmapSource>>? node))
                    return null;
                recent.Remove(node);
                recent.AddFirst(node);
                return node.Value.Value;
            }
        }

        private void Keep(string path, BitmapSource image)
        {
            lock (cached)
            {
                if (cached.ContainsKey(path))
                    return;
                cached[path] = recent.AddFirst(new KeyValuePair<string, BitmapSource>(path, image));
                while (cached.Count > capacity)
                {
                    LinkedListNode<KeyValuePair<string, BitmapSource>> oldest = recent.Last!;
                    recent.RemoveLast();
                    cached.Remove(oldest.Value.Key);
                }
            }
        }

        private static BitmapSource? Decode(string path)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = DecodeWidth;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or ArgumentException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
