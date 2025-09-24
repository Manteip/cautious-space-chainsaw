using Endpoint.Flash.Core.Domain;
using Endpoint.Flash.Core.Extensions.Redis.Interface;
using StackExchange.Redis.Extensions.Core.Configuration;
using System.Collections.Concurrent;


namespace Endpoint.Flash.Core.Extensions.Redis
{
    public class CacheConnector : ICacheConnector
    {
        /// <summary>
        /// Static dictionary to hold all redis connections.  Dictionary key is the 
        /// redis cache connection string
        /// </summary>
        private ConcurrentDictionary<string, ConnectionMultiplexer>? redisConnections;
        private readonly Settings _settings;
        public ConnectionMultiplexer connectionMultiplexer;
        public CacheConnector(Settings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(_settings.RedisCacheConnectionString))
                throw new ArgumentException(nameof(_settings.RedisCacheConnectionString));

            connectionMultiplexer = GetConnection(_settings.RedisCacheConnectionString);

        }

        /// <inheritdoc/>
        public ConnectionMultiplexer GetConnection(string cacheConnectionString)
        {
            //instantiate dictionary if null
            redisConnections ??= new ConcurrentDictionary<string, ConnectionMultiplexer>();

            ConnectionMultiplexer connection;
            if (!redisConnections.TryGetValue(cacheConnectionString, out connection!))
            {
                var redisConfiguration = new RedisConfiguration();
                redisConfiguration.AbortOnConnectFail = _settings.RedisAbortOnConnectFail; // Prevent exceptions on connection failure
                redisConfiguration.PoolSize = _settings.RedisPoolSize;                
                redisConfiguration.ConnectionString = cacheConnectionString;
                connection = ConnectionMultiplexer.Connect(redisConfiguration.ConfigurationOptions);
                redisConnections.AddOrUpdate(cacheConnectionString, connection, (oldConnection, newConnection) => connection);
            }

            return connection;
        }

        /// <inheritdoc/>
        public IDatabase GetDbCache(string cacheConnectionString)
        {
            var connection = GetConnection(cacheConnectionString);
            IDatabase dbCache = connection.GetDatabase();
            return dbCache;
        }
    }
}