// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Microsoft.StringTools
{
    /// <summary>
    /// Implements interning based on a WeakStringCache.
    /// </summary>
    internal class WeakStringCacheInterner : IDisposable
    {
        /// <summary>
        /// Enumerates the possible interning results.
        /// </summary>
        private enum InternResult
        {
            FoundInWeakStringCache,
            AddedToWeakStringCache,
        }

        internal static WeakStringCacheInterner Instance = new WeakStringCacheInterner();

        /// <summary>
        /// The cache to keep strings in.
        /// </summary>
        private readonly WeakStringCache _weakStringCache = new WeakStringCache();

#region Statistics
        /// <summary>
        /// Whether or not to gather statistics.
        /// </summary>
        private volatile bool _gatherStatistics;

        /// <summary>
        /// Number of times the regular interning path found the string in the cache.
        /// </summary>
        private int _regularInternHits;

        /// <summary>
        /// Number of times the regular interning path added the string to the cache.
        /// </summary>
        private int _regularInternMisses;

        /// <summary>
        /// Total number of strings eliminated by interning.
        /// </summary>
        private int _internEliminatedStrings;

        /// <summary>
        /// Total number of chars eliminated across all strings.
        /// </summary>
        private int _internEliminatedChars;

        /// <summary>
        /// Maps strings that went though the interning path to the number of times they have been
        /// seen. The higher the number the better the payoff of interning.
        /// </summary>
        private Dictionary<string, int> _internCallCountsByString;

#endregion

        /// <summary>
        /// Try to intern the string.
        /// The return value indicates the how the string was interned.
        /// </summary>
        private InternResult Intern(ref InternableString candidate, out string interned)
        {
            interned = _weakStringCache.GetOrCreateEntry(ref candidate, out bool cacheHit);
            return cacheHit ? InternResult.FoundInWeakStringCache : InternResult.AddedToWeakStringCache;
        }

        /// <summary>
        /// Intern the given InternableString.
        /// </summary>
        public string InternableToString(ref InternableString candidate)
        {
            if (candidate.Length == 0)
            {
                return string.Empty;
            }

            InternResult resultForStatistics = Intern(ref candidate, out string internedString);
#if DEBUG
            string expectedString = candidate.ExpensiveConvertToString();
            if (!String.Equals(internedString, expectedString))
            {
                throw new InvalidOperationException(String.Format("Interned string {0} should have been {1}", internedString, expectedString));
            }
#endif

            if (_gatherStatistics)
            {
                lock (_internCallCountsByString)
                {
                    switch (resultForStatistics)
                    {
                        case InternResult.FoundInWeakStringCache:
                            _regularInternHits++;
                            break;
                        case InternResult.AddedToWeakStringCache:
                            _regularInternMisses++;
                            break;
                    }

                    _internCallCountsByString.TryGetValue(internedString, out int priorCount);
                    _internCallCountsByString[internedString] = priorCount + 1;

                    if (!candidate.ReferenceEquals(internedString))
                    {
                        // Reference changed so 'candidate' is now released and should save memory.
                        _internEliminatedStrings++;
                        _internEliminatedChars += candidate.Length;
                    }
                }
            }

            return internedString;
        }

        /// <summary>
        /// 
        /// </summary>
        public void EnableStatistics()
        {
            _internCallCountsByString = new Dictionary<string, int>();
            _gatherStatistics = true;
        }

        /// <summary>
        /// Returns a string with human-readable statistics.
        /// </summary>
        public string FormatStatistics()
        {
            StringBuilder result = new StringBuilder(1024);

            string title = "Opportunistic Intern";

            if (_gatherStatistics)
            {
                result.AppendLine(string.Format("\n{0}{1}{0}", new string('=', 41 - (title.Length / 2)), title));
                result.AppendLine(string.Format("||{0,50}|{1,20:N0}|{2,8}|", "WeakStringCache Hits", _regularInternHits, "hits"));
                result.AppendLine(string.Format("||{0,50}|{1,20:N0}|{2,8}|", "WeakStringCache Misses", _regularInternMisses, "misses"));
                result.AppendLine(string.Format("||{0,50}|{1,20:N0}|{2,8}|", "Eliminated Strings*", _internEliminatedStrings, "strings"));
                result.AppendLine(string.Format("||{0,50}|{1,20:N0}|{2,8}|", "Eliminated Chars", _internEliminatedChars, "chars"));
                result.AppendLine(string.Format("||{0,50}|{1,20:N0}|{2,8}|", "Estimated Eliminated Bytes", _internEliminatedChars * 2, "bytes"));
                result.AppendLine("Elimination assumes that strings provided were unique objects.");
                result.AppendLine("|---------------------------------------------------------------------------------|");

                IEnumerable<string> topMissingHardcodedString =
                    _internCallCountsByString
                    .OrderByDescending(kv => kv.Value * kv.Key.Length)
                    .Take(15)
                    .Where(kv => kv.Value > 1)
                    .Select(kv => string.Format(CultureInfo.InvariantCulture, "({1} instances x each {2} chars)\n{0}", kv.Key, kv.Value, kv.Key.Length));

                result.AppendLine(string.Format("##########Top Missing Hardcoded Strings:  \n{0} ", string.Join("\n==============\n", topMissingHardcodedString.ToArray())));
                result.AppendLine();

                WeakStringCache.DebugInfo debugInfo = _weakStringCache.GetDebugInfo();
                result.AppendLine("WeakStringCache statistics:");
                result.AppendLine(string.Format("String count live/collected/total = {0}/{1}/{2}", debugInfo.LiveStringCount, debugInfo.CollectedStringCount, debugInfo.LiveStringCount + debugInfo.CollectedStringCount));
            }
            else
            {
                result.Append(title);
                result.AppendLine(" - EnableStatisticsGathering() has not been called");
            }

            return result.ToString();
        }

        /// <summary>
        /// Releases all strings from the underlying intern table.
        /// </summary>
        public void Dispose()
        {
            _weakStringCache.Dispose();
        }
    }
}
