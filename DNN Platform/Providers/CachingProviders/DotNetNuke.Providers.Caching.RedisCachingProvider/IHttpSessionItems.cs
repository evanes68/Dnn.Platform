// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information
namespace DotNetNuke.Providers.Caching.RedisCachingProvider.Interfaces
{
    using System.Collections.Concurrent;

    /// <summary>
    /// Interface to store items.
    /// </summary>
    public interface IHttpSessionItems
    {
        /// <summary>
        /// Gets items collection.
        /// </summary>
        ConcurrentDictionary<string, object> Items { get; }

        /// <summary>
        /// Add item to store.
        /// </summary>
        /// <param name="psKey">Key.</param>
        /// <param name="poValue">Value.</param>
        void AddItem(string psKey, object poValue);

        /// <summary>
        /// Remove item from store.
        /// </summary>
        /// <param name="psKey">Key.</param>
        void RemoveItem(string psKey);
    }
}
