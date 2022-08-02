// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information
namespace DotNetNuke.Providers.Caching.RedisCachingProvider
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using DotNetNuke.Common.Utilities;
    using DotNetNuke.Instrumentation;
    using DotNetNuke.Providers.Caching.RedisCachingProvider.Interfaces;

    /// <summary>
    /// The ContextCache stores items per Request in the HttpContext.Current.Items object.
    /// </summary>
    public class CacheUtility
    {
        /// <summary>
        /// Cachetime.
        /// </summary>
        public const long CACHETIME01MINUTE = 60;

        /// <summary>
        /// Cachetime.
        /// </summary>
        public const long CACHETIME05MINUTES = 300;

        /// <summary>
        /// Cachetime.
        /// </summary>
        public const long CACHETIME15MINUTES = 900;

        /// <summary>
        /// Cachetime.
        /// </summary>
        public const long CACHETIME60MINUTES = 3600;

        private static readonly ILog Log = LoggerSource.Instance.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Gets cache object.
        /// </summary>
        public static RedisCachingProvider Cache
        {
            get { return (RedisCachingProvider)RedisCachingProvider.Instance(); }
        }

        /// <summary>
        /// * DNN zet in de cache functies prefix DNN_ voor de key.Als wij via hier iets in Redis willen zetten, moeten wij het maar met ECM_ prefixen zodat de RedisCachingProvider 'm later vindt DNN_ECM_ (useLocalDict)
        /// * Bedoeling van deze functie: forceer opslaan in Redis wanneer we dat willen.
        /// * Ook: pnCacheTime naar seconden.
        /// </summary>
        /// <typeparam name="T">Type.</typeparam>
        /// <param name="psKey">Key.</param>
        /// <param name="poFunction">Function.</param>
        /// <param name="pnCacheTime">Cachetime.</param>
        /// <param name="pbForceRedis">Force update redis.</param>
        /// <param name="pbUseContextCache">Use contextCache.</param>
        /// <param name="pbForceUpdate">ForceUpdate.</param>
        /// <param name="httpSessionItems">Httpsession.</param>
        /// <returns>Object from cache.</returns>
        public static T SmartCacheObject<T>(string psKey, Func<T> poFunction, long pnCacheTime, bool pbForceRedis = false, bool pbUseContextCache = false, bool pbForceUpdate = false, IHttpSessionItems httpSessionItems = null)
        {
            psKey = pbForceRedis && !psKey.StartsWith("ECM_") ? "ECM_" + psKey : psKey;
            return SmartCacheObject(psKey, poFunction, pnCacheTime != 0 ? TimeSpan.FromSeconds(pnCacheTime) : (TimeSpan?)null, pbUseContextCache, pbForceUpdate, httpSessionItems);
        }

        /// <summary>
        /// *    The SmartCache is a wrapper around the cache.It has a notion of
        /// * ContextCache => Objects that are only available in the Current Request
        /// * Explicitly when we set flag pbUseContext to "true"
        ///  *       RedisCache => Objects stored in Redis
        ///  * Defined when there is no "key" prefix QUICK_ ?.
        /// </summary>
        /// <typeparam name="T">Type.</typeparam>
        /// <param name="psKey">Key.</param>
        /// <param name="poFunction">Function.</param>
        /// <param name="pnCacheTime">CacheTime.</param>
        /// <param name="pbUseContextCache">Use context cache.</param>
        /// <param name="pbForceUpdate">Force update.</param>
        /// <param name="httpSessionItems">Httpsession.</param>
        /// <returns>Object from cache.</returns>
        public static T SmartCacheObject<T>(string psKey, Func<T> poFunction, TimeSpan? pnCacheTime = null, bool pbUseContextCache = false, bool pbForceUpdate = false, IHttpSessionItems httpSessionItems = null)
        {
            object loReturn = null;

            try
            {
                if (!pbForceUpdate)
                {
                    // If we use ContextCache, check that our object is available there
                    if (pbUseContextCache)
                    {
                        httpSessionItems?.Items?.TryGetValue(psKey, out loReturn);

                        if (loReturn != null)
                        {
                            return GetCacheDataConvert<T>((T)loReturn);
                        }
                    }

                    // If we don't have our object, or if we don't use ContextCache, check DataCache
                    loReturn = DataCache.GetCache(psKey);
                }

                // If we don't have our object, get it from poFunction
                if (loReturn == null)
                {
                    if (poFunction != null)
                    {
                        loReturn = poFunction();

                        // If we have our object, and we mean to cache it, cache it.
                        // Note that even if we have our object from cache, we insert it again. (reset the timespan)
                        //    => We do not do this, but for Redis, we could call EXPIRE to set a new expiry for existing key
                        if (loReturn != null && pnCacheTime != null && pnCacheTime.HasValue)
                        {
                            TimeSpan lnCacheTime = pnCacheTime.Value;
                            DataCache.SetCache(psKey, loReturn, lnCacheTime);
                            string lsBaseCompanyId = psKey.IndexOf('.') > -1 ? psKey.Substring(0, psKey.IndexOf('.')).Substring("ECM_".Length) : string.Empty;

                            var sessionItems = httpSessionItems?.Items["REDIS_KEYS_" + lsBaseCompanyId];
                            if (sessionItems is List<string>)
                            {
                                if (!(sessionItems as List<string>).Contains(psKey))
                                {
                                    (sessionItems as List<string>).Add(psKey);
                                }
                            }
                        }
                        else if (loReturn == null && pbForceUpdate)
                        {
                            // Forcing an update with a null value, explicitely delete
                            DataCache.RemoveCache(psKey);
                            httpSessionItems.RemoveItem(psKey);

                            // HttpContext.Current.Items.Remove(psKey);
                        }
                    }
                }

                if (pbUseContextCache && httpSessionItems?.Items != null)
                {
                    httpSessionItems.AddItem(psKey, loReturn);
                }

                // And if we use ContextCache, store our object in there.
                // if (pbUseContextCache && HttpContext.Current?.Items != null && loReturn != null) { HttpContext.Current.Items[psKey] = loReturn; }
            }
            catch (Exception ex)
            {
                Log.Error("GetObjectFromCache " + psKey, ex);
            }

            return GetCacheDataConvert<T>(loReturn);
        }

        /// <summary>
        /// Get Set members with key.
        /// </summary>
        /// <param name="key">Key.</param>
        /// <returns>List of members.</returns>
        public static List<string> SetMembers(string key)
        {
            var loObj = Cache.PublicRedisCache.SetMembers(RedisCachingProvider.KeyPrefix + key);
            return loObj.Select(member => (string)member).ToList();
        }

        /// <summary>
        /// Publish message to channel.
        /// </summary>
        /// <param name="channel">Channel.</param>
        /// <param name="message">Message.</param>
        public static void Publish(string channel, string message)
        {
            Cache.PublicRedisCache.Publish(channel, message);
        }

        /// <summary>
        /// Get String from cache.
        /// </summary>
        /// <param name="key">Key.</param>
        /// <returns>String.</returns>
        public static string StringGet(string key)
        {
            return Cache.PublicRedisCache.StringGet(key);
        }

        /// <summary>
        /// Set string in cache.
        /// </summary>
        /// <param name="key">Key.</param>
        /// <param name="value">Value.</param>
        /// <param name="t">Timespan.</param>
        public static void StringSet(string key, string value, TimeSpan t)
        {
            Cache.PublicRedisCache.StringSet(key, value, t);
        }

        /// <summary>
        /// Wrapper for 'Session' logica but using REDIS and HttpContext instead. This retrieves data from cache, but sets it if not already present.
        /// </summary>
        /// <typeparam name="T">Type of the variable you want to get.</typeparam>
        /// <param name="pnBaseCompanyId">Base companyid.</param>
        /// <param name="psCategory">Used to create a unique key in REDIS. e.g. "ADDCUSTOMER".</param>
        /// <param name="psKey">Used to create a unique key in REDIS. e.g. "RESULT".</param>
        /// <param name="psPortalGUID">Used to create a unique key in REDIS. Ensures keys are unique per portal.</param>
        /// <param name="psUsername">Used to create a unique key in REDIS. Ensures keys are unique per user.</param>
        /// <param name="poFunc">The function to call when the data is not in cache.</param>
        /// <param name="pnCacheTime">Time in cache.</param>
        /// <returns>Object from cache.</returns>
        public static T SessionGet<T>(long pnBaseCompanyId, string psCategory, string psKey, string psPortalGUID, string psUsername, Func<T> poFunc, long pnCacheTime = CACHETIME60MINUTES)
        {
            return SmartCacheObject(
                $"ECM_{pnBaseCompanyId}.SESSION_{psCategory}_{psKey}_{psPortalGUID}_{psUsername}",
                poFunc,
                pnCacheTime,
                pbForceRedis: true,
                pbUseContextCache: true);
        }

        /// <summary>
        /// Wrapper for 'Session' logica but using REDIS and HttpContext instead. This forces an update to cache, and returns it to confirm.
        /// </summary>
        /// <typeparam name="T">Type of the variable you want to set.</typeparam>
        /// <param name="pnBaseCompanyId">Base companyid.</param>
        /// <param name="psCategory">Used to create a unique key in REDIS. e.g. "ADDCUSTOMER".</param>
        /// <param name="psKey">Used to create a unique key in REDIS. e.g. "RESULT".</param>
        /// <param name="psPortalGUID">Used to create a unique key in REDIS. Ensures keys are unique per portal.</param>
        /// <param name="psUsername">Used to create a unique key in REDIS. Ensures keys are unique per user.</param>
        /// <param name="poFunc">The function to call when the data is not in cache.</param>
        /// <param name="pnCacheTime">Time in cache.</param>
        /// <returns>Object from cache.</returns>
        public static T SessionSet<T>(long pnBaseCompanyId, string psCategory, string psKey, string psPortalGUID, string psUsername, Func<T> poFunc, long pnCacheTime = CACHETIME60MINUTES)
        {
            return SmartCacheObject(
                $"ECM_{pnBaseCompanyId}.SESSION_{psCategory}_{psKey}_{psPortalGUID}_{psUsername}",
                poFunc,
                pnCacheTime,
                pbForceRedis: true,
                pbUseContextCache: true,
                pbForceUpdate: true);
        }

        private static T GetCacheDataConvert<T>(object poItem)
        {
            if (poItem == null)
            {
                return default(T);
            }

            var loType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T)Convert.ChangeType(poItem, loType);
        }
    }
}
