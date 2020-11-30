// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

#if !NET35
using Microsoft.Extensions.ObjectPool;
#endif

namespace Microsoft.StringTools
{
    /// <summary>
    /// A callback type to register with the library. The library will invoke this callback in registration order until
    /// true is returned. If no callback returns true, the library will fall back to its default interning logic.
    /// </summary>
    /// <param name="internableString">The string to be interned.</param>
    /// <param name="result">The interned string. Set to null and return true if the string should not be interned.</param>
    /// <returns>True if the <paramref name="internableString" /> was handled by the callback.</returns>
    public delegate bool TryInternStringDelegate(ref InternableString internableString, out string result);

    public static class Strings

    {
#if !NET35
        /// <summary>
        /// IPooledObjectPolicy used by <cref see="s_stringBuilderPool"/>.
        /// </summary>
        private class PooledObjectPolicy : IPooledObjectPolicy<SpanBasedStringBuilder>
        {
            /// <summary>
            /// No need to retain excessively long builders forever.
            /// </summary>
            private const int MAX_RETAINED_BUILDER_CAPACITY = 1000;

            /// <summary>
            /// Creates a new SpanBasedStringBuilder with the default capacity.
            /// </summary>
            public SpanBasedStringBuilder Create()
            {
                return new SpanBasedStringBuilder();
            }

            /// <summary>
            /// Returns a builder to the pool unless it's excessively long.
            /// </summary>
            public bool Return(SpanBasedStringBuilder stringBuilder)
            {
                if (stringBuilder.Capacity <= MAX_RETAINED_BUILDER_CAPACITY)
                {
                    stringBuilder.Clear();
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// A pool of SpanBasedStringBuilders as we don't want to be allocating every time a new one is requested.
        /// </summary>
        private static DefaultObjectPool<SpanBasedStringBuilder> s_stringBuilderPool =
            new DefaultObjectPool<SpanBasedStringBuilder>(new PooledObjectPolicy(), Environment.ProcessorCount);
#endif

        /// <summary>
        /// An array of callbacks to be called for each string being interned.
        /// </summary>
        private static volatile TryInternStringDelegate[] s_internStringCallbacks = new TryInternStringDelegate[0];

        /// <summary>
        /// A lock protecting writes to <see cref="s_internStringCallbacks"/>.
        /// </summary>
        private static object s_locker = new object();


        #region Public methods

        /// <summary>
        /// Interns the given string if possible.
        /// </summary>
        /// <param name="str">The string to intern.</param>
        /// <returns>The interned string equal to <paramref name="str"/>.</returns>
        /// <remarks>
        /// <see cref="TryInternStringDelegate"/> callbacks registered via <see cref="RegisterStringInterningCallback"/> are called
        /// in registration order to check if custom interning is desired. If no callback returns true, the default interner based
        /// on weak GC handles is used.
        /// </remarks>
        public static string TryIntern(string str)
        {
            InternableString internableString = new InternableString(str);
            return OpportunisticIntern.Instance.InternableToString(ref internableString);
        }

#if !NET35
        /// <summary>
        /// Interns the given readonly character span if possible.
        /// </summary>
        /// <param name="str">The character span to intern.</param>
        /// <returns>The interned string equal to <paramref name="str"/>.</returns>
        /// <remarks>
        /// <see cref="TryInternStringDelegate"/> callbacks registered via <see cref="RegisterStringInterningCallback"/> are called
        /// in registration order to check if custom interning is desired. If no callback returns true, the default interner based
        /// on weak GC handles is used.
        /// </remarks>
        public static string TryIntern(ReadOnlySpan<char> str)
        {
            InternableString internableString = new InternableString(str);
            return OpportunisticIntern.Instance.InternableToString(ref internableString);
        }
#endif

        /// <summary>
        /// Returns a new or recycled <see cref="SpanBasedStringBuilder"/>.
        /// </summary>
        /// <returns>The SpanBasedStringBuilder.</returns>
        /// <remarks>
        /// Call <see cref="IDisposable.Dispose"/> on the returned instance to recycle it.
        /// </remarks>
        public static SpanBasedStringBuilder GetSpanBasedStringBuilder()
        {
#if NET35
            return new SpanBasedStringBuilder();
#else
            return s_stringBuilderPool.Get();
#endif
        }

        /// <summary>
        /// Adds a callback to be called when a string is being interned.
        /// </summary>
        /// <param name="callback">The callback to add.</param>
        /// <remarks>
        /// Use this to implement custom interning for some strings. The callback has access to the string being interned
        /// via the <see cref="InternableString"/> representation and can override the default behavior by returning a custom
        /// string.
        /// </remarks>
        public static void RegisterStringInterningCallback(TryInternStringDelegate callback)
        {
            lock (s_locker)
            {
                if (!s_internStringCallbacks.Any(existingCallback => existingCallback == callback))
                {
                    TryInternStringDelegate[] newInternStringCallback = new TryInternStringDelegate[s_internStringCallbacks.Length + 1];
                    s_internStringCallbacks.CopyTo(newInternStringCallback, 0);
                    newInternStringCallback[s_internStringCallbacks.Length] = callback;

                    s_internStringCallbacks = newInternStringCallback;
                }
            }
        }

        /// <summary>
        /// Removes a callback previously added with <see cref="RegisterStringInterningCallback"/>.
        /// </summary>
        /// <param name="callback">The callback to remove.</param>
        public static void UnregisterStringInterningCallback(TryInternStringDelegate callback)
        {
            lock (s_locker)
            {
                s_internStringCallbacks = s_internStringCallbacks.Where(existingCallback => existingCallback != callback).ToArray();
            }
        }

        /// <summary>
        /// Enables diagnostics in the interner. Call <see cref="CreateDiagnosticReport"/> to retrieve the diagnostic data.
        /// </summary>
        public static void EnableDiagnostics()
        {
            OpportunisticIntern.Instance.EnableStatisticsGathering();
        }

        /// <summary>
        /// Retrieves the diagnostic data describing the current state of the interner. Make sure to call <see cref="EnableDiagnostics"/> beforehand.
        /// </summary>
        public static string CreateDiagnosticReport()
        {
            StringBuilder callbackReport = new StringBuilder();
            callbackReport.AppendFormat("{0} with {1} string interning callbacks registered", nameof(Strings), s_internStringCallbacks.Length);
            callbackReport.AppendLine();

            return callbackReport.ToString() + OpportunisticIntern.Instance.FormatStatistics();
        }

        #endregion

        /// <summary>
        /// Returns a <see cref="SpanBasedStringBuilder"/> instance back to the pool if possible.
        /// </summary>
        /// <param name="stringBuilder">The instance to return.</param>
        internal static void ReturnSpanBasedStringBuilder(SpanBasedStringBuilder stringBuilder)
        {
            if (stringBuilder == null)
            {
                throw new ArgumentNullException(nameof(stringBuilder));
            }
#if !NET35
            s_stringBuilderPool.Return(stringBuilder);
#endif
        }

        /// <summary>
        /// Calls interning callbacks in sequence until one return true.
        /// </summary>
        /// <param name="candidate">The candidate internable string to invoke the callbacks with.</param>
        /// <param name="interned">The resulting string.</param>
        /// <returns></returns>
        internal static bool CallStringInterningCallbacks(ref InternableString candidate, out string interned)
        {
            // We can read s_internStringCallbacks lock-less because it's declared volatile and RegisterStringInterningCallback
            // and UnregisterStringInterningCallback write the field after initializing its content.
            TryInternStringDelegate[] callbacks = s_internStringCallbacks;
            foreach (TryInternStringDelegate callback in callbacks)
            {
                if (callback(ref candidate, out interned))
                {
                    return true;
                }
            }
            interned = null;
            return false;
        }

        /// <summary>
        /// Resets the registered callbacks for testing purposes.
        /// </summary>
        internal static void ResetForTests()
        {
            s_internStringCallbacks = new TryInternStringDelegate[0];
        }
    }
}
