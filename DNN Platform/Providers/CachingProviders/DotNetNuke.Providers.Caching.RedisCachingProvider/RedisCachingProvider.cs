// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information
namespace DotNetNuke.Providers.Caching.RedisCachingProvider
{
    using System;
    using System.Configuration;
    using System.Globalization;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Formatters;
    using System.Runtime.Serialization.Formatters.Binary;
    using System.Web.Caching;
    using System.Xml;

    using DotNetNuke.Common.Utilities;
    using DotNetNuke.Instrumentation;
    using DotNetNuke.Services.Cache;
    using StackExchange.Redis;

    /// <summary>
    /// Deze class gebruiken we om via redis berichten te ontvangen van andere webservers, denk aan clear of remove key. Het daadwerkelijk opslaan
    /// van objecten wordt in de interne DNN cache gedaan in System.Web.Caching.Cache.
    /// </summary>
    public class RedisCachingProvider : CachingProvider
    {
        private const string ProviderName = "RedisCachingProvider";
        private const bool DefaultUseCompression = false;
        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(typeof(RedisCachingProvider));
        private static readonly Lazy<ConnectionMultiplexer> LazyConnection = new Lazy<ConnectionMultiplexer>(() =>
        {
            var cn = ConnectionMultiplexer.Connect(ConnectionString);
            cn.GetSubscriber()
             .Subscribe(
                 new RedisChannel(KeyPrefix + "Redis.*", RedisChannel.PatternMode.Pattern),
                 ProcessMessage);
            return cn;
        });

        private static string keyPrefix;
        private static IDatabase redisCache;

        /// <summary>
        /// Gets uniek per dnn database. Elke webserver dus zelfde keyprefix. Dit is gewenst zodat een sleutel aangemaakt door 1 webserver wordt hergebruikt door de ander.
        /// </summary>
        public static string KeyPrefix
        {
            get
            {
                return string.IsNullOrEmpty(keyPrefix) ? keyPrefix = ECM_Version : keyPrefix;
            }
        }

        /// <summary>
        /// Gets applicatie Versie.
        /// </summary>
        public static string ECM_Version
        {
            get
            {
                return "1.0";
            }
        }

        /// <summary>
        /// Gets redis cache object.
        /// </summary>
        public IDatabase PublicRedisCache
        {
            get { return RedisCache; }
        }

        private static IDatabase RedisCache
        {
            get { return redisCache ?? (redisCache = Connection.GetDatabase()); }
        }

        private static string ConnectionString
        {
            get
            {
                var cs = ConfigurationManager.ConnectionStrings["RedisCachingProvider"];
                if (cs == null || string.IsNullOrEmpty(cs.ConnectionString))
                {
                    throw new ConfigurationErrorsException("The Redis connection string can't be an empty string. Check the RedisCachingProvider connectionString attribute in your web.config file.");
                }

                return cs.ConnectionString;
            }
        }

        private static ConnectionMultiplexer Connection
        {
            get { return LazyConnection.Value; }
        }

        /// <summary>
        /// Gets een uniek kenmerk per webserver.
        /// </summary>
        private static string InstanceUniqueId
        {
            get
            {
                // Process.GetCurrentProcess().Id hebben we vervangen door 0 omdat de machine naam nu al uniek genoeg is.
                return string.Format("{0}_Process_{1:D6}", DotNetNuke.Common.Globals.ServerName, 0);
            }
        }

        /// <summary>
        /// Set object in cache.
        /// </summary>
        /// <param name="psKey">Key.</param>
        /// <param name="psValue">Value.</param>
        /// <param name="lnSeconds">How long to keep item in cache.</param>
        /// <returns>Value from cache.</returns>
        public static string DirectSet(string psKey, string psValue, long lnSeconds)
        {
            RedisCache.StringSet(psKey, psValue, TimeSpan.FromSeconds(lnSeconds));
            return psValue;
        }

        /// <summary>
        /// Get object from cache.
        /// </summary>
        /// <param name="psKey">Key.</param>
        /// <returns>Value from cache.</returns>
        public static string DirectGet(string psKey)
        {
            return RedisCache.StringGet(psKey);
        }

        /// <summary>
        /// Serialize object.
        /// </summary>
        /// <param name="source">Object Source.</param>
        /// <returns>Serialized object.</returns>
        public static string Serialize(object source)
        {
            IFormatter formatter = new BinaryFormatter();
            var stream = new MemoryStream();
            formatter.Serialize(stream, source);
            return Convert.ToBase64String(stream.ToArray());
        }

        /// <summary>
        /// Deserialize object.
        /// </summary>
        /// <typeparam name="T">Type.</typeparam>
        /// <param name="base64String">Value.</param>
        /// <returns>Object.</returns>
        public static T Deserialize<T>(string base64String)
        {
            var stream = new MemoryStream(Convert.FromBase64String(base64String));
            IFormatter formatter = new BinaryFormatter();
            stream.Position = 0;
            return (T)formatter.Deserialize(stream);
        }

        /// <summary>
        /// Deserialize object.
        /// </summary>
        /// <param name="base64String">Value.</param>
        /// <returns>Object.</returns>
        public static object Deserialize(string base64String)
        {
            var stream = new MemoryStream(Convert.FromBase64String(base64String));
            IFormatter formatter = new BinaryFormatter();
            stream.Position = 0;
            return formatter.Deserialize(stream);
        }

        /// <summary>
        /// Serialize XML object.
        /// </summary>
        /// <param name="obj">Object.</param>
        /// <returns>Array of Byte.</returns>
        public static byte[] SerializeXmlBinary(object obj)
        {
            using (var ms = new MemoryStream())
            {
                using (var wtr = XmlDictionaryWriter.CreateBinaryWriter(ms))
                {
                    var serializer = new NetDataContractSerializer();
                    serializer.WriteObject(wtr, obj);
                    ms.Flush();
                }

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Deserialize XMl object.
        /// </summary>
        /// <param name="bytes">Array of bytes.</param>
        /// <returns>Object.</returns>
        public static object DeSerializeXmlBinary(byte[] bytes)
        {
            using (var rdr = XmlDictionaryReader.CreateBinaryReader(bytes, XmlDictionaryReaderQuotas.Max))
            {
                var serializer = new NetDataContractSerializer { AssemblyFormat = FormatterAssemblyStyle.Simple };
                return serializer.ReadObject(rdr);
            }
        }

        /// <summary>
        /// Insert object into cache.
        /// </summary>
        /// <param name="key">Key.</param>
        /// <param name="value">Value.</param>
        /// <param name="dependency">Dependency.</param>
        /// <param name="absoluteExpiration">Expiration.</param>
        /// <param name="slidingExpiration">Sliding Expiration.</param>
        /// <param name="priority">Priority.</param>
        /// <param name="onRemoveCallback">Callback.</param>
        public override void Insert(string key, object value, DNNCacheDependency dependency, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, CacheItemRemovedCallback onRemoveCallback)
        {
            if (Logger.IsDebugEnabled)
            {
                Logger.Debug("Redis::Insert::" + key);
            }

            try
            {
                TimeSpan? expiry = null; // Calculate expiry.
                if (absoluteExpiration != Cache.NoAbsoluteExpiration)
                {
                    expiry = absoluteExpiration.Subtract(DateTime.UtcNow);
                }
                else if (slidingExpiration != Cache.NoSlidingExpiration)
                {
                    expiry = slidingExpiration;
                }

                if (key.StartsWith("ECM"))
                {
                    var storeobject = Serialize(value);
                    RedisCache.StringSet(key, storeobject, expiry);
                }
                else
                {
                    base.Insert(key, value, dependency, absoluteExpiration, slidingExpiration, priority, onRemoveCallback);
                }
            }
            catch (Exception e)
            {
                if (!ProcessException(e, key, value))
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Gets the item. Note: sliding expiration not implemented to avoid too many requests to the redis server
        /// (see http://stackoverflow.com/questions/20280316/absolute-and-sliding-caching-in-redis).
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>Object from cache.</returns>
        public override object GetItem(string key)
        {
            if (Logger.IsDebugEnabled)
            {
                if (key.StartsWith("DNN_PersonaBarMenuPermissionsXXX"))
                {
                    System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
                    Logger.Debug("Redis::GetItem::" + key + " " + t.ToString());
                }
                else
                {
                    // Logger.Debug("Redis::GetItem::" + key);
                }
            }

            try
            {
                if (key.StartsWith("ECM"))
                {
                    var value = RedisCache.StringGet(key);
                    if (value.HasValue)
                    {
                        return Deserialize(value);
                    }

                    return null;
                }
                else
                {
                    var value = base.GetItem(key);
                    return value;
                }
            }
            catch (Exception e)
            {
                if (!ProcessException(e))
                {
                    throw;
                }
            }

            return null;
        }

        /// <summary>
        /// Clear item from cache.
        /// </summary>
        /// <param name="type">Item type.</param>
        /// <param name="data">Item data.</param>
        public override void Clear(string type, string data)
        {
            this.Clear(type, data, true);
        }

        /// <summary>
        /// Remove item from cache.
        /// </summary>
        /// <param name="key">Item key.</param>
        public override void Remove(string key)
        {
            this.Remove(key, true);
        }

        /// <summary>
        /// Clear item from cache.
        /// </summary>
        /// <param name="type">Item type.</param>
        /// <param name="data">Item data.</param>
        /// <param name="notifyRedis">Tell redis.</param>
        internal void Clear(string type, string data, bool notifyRedis)
        {
            try
            {
                if (Logger.IsDebugEnabled)
                {
                    Logger.Debug(string.Format("{0} - Clearing local cache (type:{1}; data:{2})...", InstanceUniqueId, type, data));
                }

                // Clear internal cache
                this.ClearCacheInternal(type, data, true);

                // Evert. Dit zorgt er nu voor dat alle sleutels worden gewist.. En dat is niet de bedoeling.
                // Wel moet het bericht naar andere servers.
                if (notifyRedis)
                {
                    // Avoid recursive calls
                    // Notify the channel
                    if (Logger.IsDebugEnabled)
                    {
                        Logger.Debug(string.Format("{0} - Notifying cache clearing to other partners...", InstanceUniqueId));
                    }

                    RedisCache.Publish(new RedisChannel(KeyPrefix + "Redis.Clear", RedisChannel.PatternMode.Auto), string.Format("{0}:{1}:{2}", InstanceUniqueId, type, data));
                }
            }
            catch (Exception e)
            {
                if (!ProcessException(e))
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Remove item from cache.
        /// </summary>
        /// <param name="key">Item key.</param>
        /// <param name="notifyRedis">Tell redis.</param>
        internal void Remove(string key, bool notifyRedis)
        {
            try
            {
                if (Logger.IsDebugEnabled)
                {
                    Logger.Debug(string.Format("{0} - Removing cache key {1}...", InstanceUniqueId, key));
                }

                // Remove from the internal cache
                this.RemoveInternal(key);

                if (notifyRedis)
                {
                    // Notify the channel
                    RedisCache.Publish(new RedisChannel(KeyPrefix + "Redis.Remove", RedisChannel.PatternMode.Auto), InstanceUniqueId + ":" + key);
                }
            }
            catch (Exception e)
            {
                if (!ProcessException(e))
                {
                    throw;
                }
            }
        }

        private static string GetProviderConfigAttribute(string attributeName, string defaultValue = "")
        {
            var provider = Config.GetProvider("caching", ProviderName);
            if (provider != null && provider.Attributes.AllKeys.Contains(attributeName))
            {
                return provider.Attributes[attributeName];
            }

            return defaultValue;
        }

        // We gebruiken deze pubsub om de applicatie- cache te flushen. Dit heeft niks met Redis te maken, behalve dat Redis het communicatie- medium tussen de servers is.
        private static void ProcessMessage(RedisChannel redisChannel, RedisValue redisValue)
        {
            try
            {
                var instance = (RedisCachingProvider)Instance() ?? new RedisCachingProvider();

                // We reageren alleen op commando's van onze eigen applicatie.
                if (!redisChannel.ToString().StartsWith(KeyPrefix))
                {
                    return;
                }

                string lsCommand = redisChannel.ToString().Substring(KeyPrefix.Length);
                switch (lsCommand)
                {
                    case "Redis.Clear":
                        // Dit is een verzoek voor een flush commando, afhankelijk van de data in het commando wordt er besloten wat er geflushed moet worden.
                        var values = redisValue.ToString().Split(':');

                        // Controleer of het verzoek niet van mezelf is! alleen van andere InstanceUniqueId gaan we verwerken!
                        if (values.Length == 3 && values[0] != InstanceUniqueId)
                        {
                            instance.Clear(values[1], values[2], false);
                        }

                        break;

                    case "Redis.Remove":
                        // Het remove command gaan we alleen verwerken als het niet van deze InstanceUniqueId is!
                        if (redisValue.ToString().Length > 0 && !redisValue.ToString().StartsWith(InstanceUniqueId))
                        {
                            // we strippen de instanceuniqueid door te zoeken naar de eerste ":"
                            string lsSubKey = redisValue.ToString();
                            int liPosition = lsSubKey.IndexOf(":");
                            if (liPosition >= 0)
                            {
                                lsSubKey = lsSubKey.Substring(liPosition + 1);
                            }
                            else
                            {
                                lsSubKey = lsSubKey.Substring(InstanceUniqueId.Length + 1);
                            }

                            // Letop de parameter false, daardoor wordt voorkomen dat een remove weer een redis bericht veroorzaakt.
                            instance.Remove(lsSubKey, false);
                        }

                        break;

                    default:
                        Logger.Warn("Unknown redis command: " + lsCommand);
                        break;
                }
            }
            catch (Exception e)
            {
                if (!ProcessException(e))
                {
                    throw;
                }
            }
        }

        private static bool ProcessException(Exception e, string key = "", object value = null)
        {
            try
            {
                if (e.GetType() != typeof(ConfigurationErrorsException) && value != null)
                {
                    Logger.Error(
                       string.Format("Error while trying to store in cache the key {0} (Object type: {1}): {2}", key, value.GetType(), e), e);
                }
                else
                {
                    Logger.Error(e.ToString());
                }

                return true;
            }
            catch (Exception)
            {
                // If the error can't be logged, allow the caller to raise the exception or not
                // so do nothing
                return false;
            }
        }
    }
}
