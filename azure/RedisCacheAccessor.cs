using Azure.Core.GeoJson;
using Endpoint.Flash.Core.Extensions.Redis.Interface;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Endpoint.Flash.Core.Extensions.Redis
{
    public class RedisCacheAccessor : IRedisCacheAccessor
    {
        private readonly ICacheConnector _cacheConnector;
        private readonly string _prefix;
        private readonly ILogger<RedisCacheAccessor> _logger;
        private readonly string _redisCacheConnectionString;

        public RedisCacheAccessor(ICacheConnector cacheConnector, string prefix, ILogger<RedisCacheAccessor> logger, string redisCacheConnectionString)
        {
            ArgumentNullException.ThrowIfNull(redisCacheConnectionString);
            _cacheConnector = cacheConnector;
            _prefix = prefix;
            _logger = logger;
            _redisCacheConnectionString = redisCacheConnectionString;

        }

        public static string BuildTrackSetKey(string tag, string docType) => $"__track__:{docType}:{{{tag}}}";
        private static readonly string emptyTrack = "__track__::{}";

        public async Task<bool> SetAsync<T>(string key, T value, TimeSpan duration)
        {
            var db = _cacheConnector.GetDbCache(_redisCacheConnectionString);
            var k = _prefix + key;
            _logger.LogDebug("Setting cache key {Key} with value {Value} for duration {Duration}", k, value, duration);

            var track = BuildTrackSetKey(ExtractHashtag(key), GetDocType(key));
            var sValue = JsonConvert.SerializeObject(value);
            key = CleanKey(key);
            var batch = db.CreateBatch();
            var t1 = batch.StringSetAsync(key, sValue, duration);

            Task t2 = Task.CompletedTask;

            if (emptyTrack != track)
            {
                t2 = batch.SetAddAsync(track, key, CommandFlags.None);
            }

            batch.Execute();
            await Task.WhenAll(t1, t2).ConfigureAwait(false);
            return t1.Result;
        }

        public Task SetMultipleAsync(KeyValuePair<RedisKey, RedisValue>[] values)
        {
            var db = _cacheConnector.GetDbCache(_redisCacheConnectionString);
            return db.StringSetAsync(values);
        }

        public async Task<T> GetSetAsync<T>(string key, Func<CancellationToken, Task<T>> calculateValue, TimeSpan duration, bool storeNull = false, CancellationToken cancellationToken = default)
        {
            // Clean and prefix the key consistently
            var cleanKey = CleanKey(key);
            var prefixedKey = _prefix + cleanKey;
            var db = _cacheConnector.GetDbCache(_redisCacheConnectionString);

            // Try to get the value from cache
            var res = await db.StringGetAsync(prefixedKey).ConfigureAwait(false);
            if (res.HasValue)
            {
                _logger.LogDebug("Cache hit for key {Key}", prefixedKey);
                return JsonConvert.DeserializeObject<T>(res!)!;
            }

            // Cache miss, calculate the value
            _logger.LogDebug("Cache miss for key {Key}", prefixedKey);
            var value = await calculateValue(cancellationToken).ConfigureAwait(false);

            // Only store in cache if the value is not null or if we're explicitly storing nulls
            if (value != null || storeNull)
            {
                var track = BuildTrackSetKey(ExtractHashtag(key), GetDocType(key));
                var batch = db.CreateBatch();

                // Set the value (either serialized object or RedisValue.Null)
                var redisValue = value != null
                    ? new RedisValue(JsonConvert.SerializeObject(value))
                    : RedisValue.Null;

                var t1 = batch.StringSetAsync(prefixedKey, redisValue, duration);

                Task t2 = Task.CompletedTask;

                if (emptyTrack != track)
                {
                    t2 = batch.SetAddAsync(track, prefixedKey, CommandFlags.None);
                }

                batch.Execute();
                await Task.WhenAll(t1, t2).ConfigureAwait(false);
            }

            return value;
        }



        public async Task<T> GetAsync<T>(string key)
        {
            var db = _cacheConnector.GetDbCache(_redisCacheConnectionString!);
            key = CleanKey(key);
            var k = _prefix + key;

            var res = await db.StringGetAsync(k).ConfigureAwait(false);
            if (res.HasValue)
            {
                _logger.LogDebug("Cache hit for key {Key}", k);
                return JsonConvert.DeserializeObject<T>(res!)!;
            }
            else
            {
                _logger.LogDebug("Cache miss for key {Key}", k);
                return default!;
            }
        }

        public async Task InvalidateAsync(string key)
        {
            _logger.LogInformation("Invalidating cache key {Key}", key);
            var db = _cacheConnector.GetDbCache(_redisCacheConnectionString);
            var k = _prefix + key;
            await db.StringSetAsync(k, RedisValue.Null);

        }

        /// <summary>
        /// Bulk-delete all keys for a tag WITHOUT Lua or SCAN.
        /// Repeatedly SPOP() up to chunkSize members from the per-tag tracking set
        /// and KeyDelete() those popped keys. Returns total deleted count.
        ///
        /// Notes:
        /// - Not strictly atomic across the whole batch (SPOP and DEL are separate calls),
        ///   but safe and idempotent for most workloads.
        /// - If other writers create new keys during deletion, those created after their
        ///   SADD won't be included unless you run again.
        /// </summary>
        public async Task<long> DeleteGroupAsync(
            string tag,
            int chunkSize = 1000,
            int maxIterations = 1_000_000,
            CommandFlags flags = CommandFlags.None,
            CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);

            var db = _cacheConnector.GetDbCache(_redisCacheConnectionString!);
            long totalDeleted = 0;

            for (int i = 0; i < maxIterations; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Pop up to chunkSize keys from the tracking set
                RedisValue[] popped = await db.SetPopAsync(tag, chunkSize, flags).ConfigureAwait(false);
                if (popped == null || popped.Length == 0)
                    break;

                // With the following line:
                var keys = popped.Select(p => (RedisKey)p.ToString()).ToArray();

                // Delete in one call (server-side will pipeline internally)
                totalDeleted += await db.KeyDeleteAsync(keys, flags).ConfigureAwait(false);
            }

            return totalDeleted;
        }


        /// <summary>
        /// Extracts the hashtag portion (content between curly braces) from a Redis key
        /// </summary>
        /// <param name="key">The key that may contain a hashtag</param>
        /// <returns>The hashtag if present, otherwise null</returns>
        private static string ExtractHashtag(string key)
        {
            var startIndex = key.IndexOf('{');
            if (startIndex >= 0)
            {
                var endIndex = key.IndexOf('}', startIndex);
                if (endIndex > startIndex)
                {
                    return key.Substring(startIndex + 1, endIndex - startIndex - 1);
                }
            }
            return string.Empty;
        }

        private static string CleanKey(string key)
        {
            return key.Replace("~", string.Empty);
        }


        private static string GetDocType(string key)
        {
            // First handle the case when key is null or empty
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            try
            {

                int hashtagEnd = key.IndexOf('}');
                if (hashtagEnd < 0) return string.Empty;
                var delimiter = '~';
                string remainingKey = key[(hashtagEnd + 1)..];
                string[] parts = remainingKey.Split(':', StringSplitOptions.RemoveEmptyEntries);

                foreach (string part in parts)
                {
                    if (part.Contains(delimiter))
                    {
                        // Extract from [d= TypeName] format
                        int startIndex = part.IndexOf(delimiter) + 1;
                        int endIndex = part.IndexOf(delimiter, startIndex);
                        if (endIndex > startIndex)
                        {
                            return part[startIndex..endIndex].Trim();
                        }
                    }
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}