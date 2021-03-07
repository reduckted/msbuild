// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Profiler;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Logging
{
    /// <summary>
    /// Serializes BuildEventArgs-derived objects into a provided BinaryWriter
    /// </summary>
    internal class BuildEventArgsWriter
    {
        private readonly Stream originalStream;

        /// <summary>
        /// When writing the current record, first write it to a memory stream,
        /// then flush to the originalStream. This is needed so that if we discover
        /// that we need to write a string record in the middle of writing the
        /// current record, we will write the string record to the original stream
        /// and the current record will end up after the string record.
        /// </summary>
        private readonly MemoryStream currentRecordStream;

        /// <summary>
        /// The binary writer around the originalStream.
        /// </summary>
        private readonly BinaryWriter originalBinaryWriter;

        /// <summary>
        /// The binary writer around the currentRecordStream.
        /// </summary>
        private readonly BinaryWriter currentRecordWriter;

        /// <summary>
        /// The binary writer we're currently using. Is pointing at the currentRecordWriter usually,
        /// but sometimes we repoint it to the originalBinaryWriter temporarily, when writing string
        /// and name-value records.
        /// </summary>
        private BinaryWriter binaryWriter;

        /// <summary>
        /// Hashtable used for deduplicating strings. When we need to write a string,
        /// we check in this hashtable first, and if we've seen the string before,
        /// just write out its index. Otherwise write out a string record, and then
        /// write the string index. A string record is guaranteed to precede its first
        /// usage.
        /// The reader will read the string records first and then be able to retrieve
        /// a string by its index. This allows us to keep the format streaming instead
        /// of writing one giant string table at the end. If a binlog is interrupted
        /// we'll be able to use all the information we've discovered thus far.
        /// </summary>
        private readonly Dictionary<HashKey, int> stringHashes = new Dictionary<HashKey, int>();

        /// <summary>
        /// Hashtable used for deduplicating name-value lists. Same as strings.
        /// </summary>
        private readonly Dictionary<HashKey, int> nameValueListHashes = new Dictionary<HashKey, int>();

        /// <summary>
        /// Index 0 is null, Index 1 is the empty string.
        /// Reserve indices 2-9 for future use. Start indexing actual strings at 10.
        /// </summary>
        internal const int StringStartIndex = 10;

        /// <summary>
        /// Let's reserve a few indices for future use.
        /// </summary>
        internal const int NameValueRecordStartIndex = 10;

        /// <summary>
        /// 0 is null, 1 is empty string
        /// 2-9 are reserved for future use.
        /// Start indexing at 10.
        /// </summary>
        private int stringRecordId = StringStartIndex;

        /// <summary>
        /// The index of the next record to be written.
        /// </summary>
        private int nameValueRecordId = NameValueRecordStartIndex;

        /// <summary>
        /// A temporary buffer we use when writing a NameValueList record. Avoids allocating a list each time.
        /// </summary>
        private readonly List<KeyValuePair<string, string>> nameValueListBuffer = new List<KeyValuePair<string, string>>(1024);

        /// <summary>
        /// A temporary buffer we use when hashing a NameValueList record. Stores the indices of hashed strings
        /// instead of the actual names and values.
        /// </summary>
        private readonly List<KeyValuePair<int, int>> nameValueIndexListBuffer = new List<KeyValuePair<int, int>>(1024);

        /// <summary>
        /// Initializes a new instance of BuildEventArgsWriter with a BinaryWriter
        /// </summary>
        /// <param name="binaryWriter">A BinaryWriter to write the BuildEventArgs instances to</param>
        public BuildEventArgsWriter(BinaryWriter binaryWriter)
        {
            this.originalStream = binaryWriter.BaseStream;

            // this doesn't exceed 30K for smaller binlogs so seems like a reasonable
            // starting point to avoid reallocations in the common case
            this.currentRecordStream = new MemoryStream(65536);

            this.originalBinaryWriter = binaryWriter;
            this.currentRecordWriter = new BinaryWriter(currentRecordStream);

            this.binaryWriter = currentRecordWriter;
        }

        /// <summary>
        /// Write a provided instance of BuildEventArgs to the BinaryWriter
        /// </summary>
        public void Write(BuildEventArgs e)
        {
            WriteCore(e);

            // flush the current record and clear the MemoryStream to prepare for next use
            currentRecordStream.WriteTo(originalStream);
            currentRecordStream.SetLength(0);
        }

        private void WriteCore(BuildEventArgs e)
        {
            switch (e)
            {
                case BuildMessageEventArgs buildMessage: Write(buildMessage); break;
                case TaskStartedEventArgs taskStarted: Write(taskStarted); break;
                case TaskFinishedEventArgs taskFinished: Write(taskFinished); break;
                case TargetStartedEventArgs targetStarted: Write(targetStarted); break;
                case TargetFinishedEventArgs targetFinished: Write(targetFinished); break;
                case BuildErrorEventArgs buildError: Write(buildError); break;
                case BuildWarningEventArgs buildWarning: Write(buildWarning); break;
                case ProjectStartedEventArgs projectStarted: Write(projectStarted); break;
                case ProjectFinishedEventArgs projectFinished: Write(projectFinished); break;
                case BuildStartedEventArgs buildStarted: Write(buildStarted); break;
                case BuildFinishedEventArgs buildFinished: Write(buildFinished); break;
                case ProjectEvaluationStartedEventArgs projectEvaluationStarted: Write(projectEvaluationStarted); break;
                case ProjectEvaluationFinishedEventArgs projectEvaluationFinished: Write(projectEvaluationFinished); break;
                default:
                    // convert all unrecognized objects to message
                    // and just preserve the message
                    var buildMessageEventArgs = new BuildMessageEventArgs(
                        e.Message,
                        e.HelpKeyword,
                        e.SenderName,
                        MessageImportance.Normal,
                        e.Timestamp);
                    buildMessageEventArgs.BuildEventContext = e.BuildEventContext ?? BuildEventContext.Invalid;
                    Write(buildMessageEventArgs);
                    break;
            }
        }

        public void WriteBlob(BinaryLogRecordKind kind, byte[] bytes)
        {
            // write the blob directly to the underlying writer,
            // bypassing the memory stream
            using var redirection = RedirectWritesToOriginalWriter();

            Write(kind);
            Write(bytes.Length);
            Write(bytes);
        }

        /// <summary>
        /// Switches the binaryWriter used by the Write* methods to the direct underlying stream writer
        /// until the disposable is disposed. Useful to bypass the currentRecordWriter to write a string,
        /// blob or NameValueRecord that should precede the record being currently written.
        /// </summary>
        private IDisposable RedirectWritesToOriginalWriter()
        {
            binaryWriter = originalBinaryWriter;
            return new RedirectionScope(this);
        }

        private struct RedirectionScope : IDisposable
        {
            private readonly BuildEventArgsWriter _writer;

            public RedirectionScope(BuildEventArgsWriter buildEventArgsWriter)
            {
                _writer = buildEventArgsWriter;
            }

            public void Dispose()
            {
                _writer.binaryWriter = _writer.currentRecordWriter;
            }
        }

        private void Write(BuildStartedEventArgs e)
        {
            Write(BinaryLogRecordKind.BuildStarted);
            WriteBuildEventArgsFields(e);
            Write(e.BuildEnvironment);
        }

        private void Write(BuildFinishedEventArgs e)
        {
            Write(BinaryLogRecordKind.BuildFinished);
            WriteBuildEventArgsFields(e);
            Write(e.Succeeded);
        }

        private void Write(ProjectEvaluationStartedEventArgs e)
        {
            Write(BinaryLogRecordKind.ProjectEvaluationStarted);
            WriteBuildEventArgsFields(e, writeMessage: false);
            WriteDeduplicatedString(e.ProjectFile);
        }

        private void Write(ProjectEvaluationFinishedEventArgs e)
        {
            Write(BinaryLogRecordKind.ProjectEvaluationFinished);

            WriteBuildEventArgsFields(e, writeMessage: false);
            WriteDeduplicatedString(e.ProjectFile);

            if (e.GlobalProperties == null)
            {
                Write(false);
            }
            else
            {
                Write(true);
                WriteProperties(e.GlobalProperties);
            }

            WriteProperties(e.Properties);

            WriteProjectItems(e.Items);

            var result = e.ProfilerResult;
            Write(result.HasValue);
            if (result.HasValue)
            {
                Write(result.Value.ProfiledLocations.Count);

                foreach (var item in result.Value.ProfiledLocations)
                {
                    Write(item.Key);
                    Write(item.Value);
                }
            }
        }

        private void Write(ProjectStartedEventArgs e)
        {
            Write(BinaryLogRecordKind.ProjectStarted);
            WriteBuildEventArgsFields(e);

            if (e.ParentProjectBuildEventContext == null)
            {
                Write(false);
            }
            else
            {
                Write(true);
                Write(e.ParentProjectBuildEventContext);
            }

            WriteDeduplicatedString(e.ProjectFile);

            Write(e.ProjectId);
            WriteDeduplicatedString(e.TargetNames);
            WriteDeduplicatedString(e.ToolsVersion);

            if (e.GlobalProperties == null)
            {
                Write(false);
            }
            else
            {
                Write(true);
                Write(e.GlobalProperties);
            }

            WriteProperties(e.Properties);

            WriteProjectItems(e.Items);
        }

        private void Write(ProjectFinishedEventArgs e)
        {
            Write(BinaryLogRecordKind.ProjectFinished);
            WriteBuildEventArgsFields(e);
            WriteDeduplicatedString(e.ProjectFile);
            Write(e.Succeeded);
        }

        private void Write(TargetStartedEventArgs e)
        {
            Write(BinaryLogRecordKind.TargetStarted);
            WriteBuildEventArgsFields(e);
            WriteDeduplicatedString(e.TargetName);
            WriteDeduplicatedString(e.ProjectFile);
            WriteDeduplicatedString(e.TargetFile);
            WriteDeduplicatedString(e.ParentTarget);
            Write((int)e.BuildReason);
        }

        private void Write(TargetFinishedEventArgs e)
        {
            Write(BinaryLogRecordKind.TargetFinished);
            WriteBuildEventArgsFields(e);
            Write(e.Succeeded);
            WriteDeduplicatedString(e.ProjectFile);
            WriteDeduplicatedString(e.TargetFile);
            WriteDeduplicatedString(e.TargetName);
            WriteTaskItemList(e.TargetOutputs);
        }

        private void Write(TaskStartedEventArgs e)
        {
            Write(BinaryLogRecordKind.TaskStarted);
            WriteBuildEventArgsFields(e);
            WriteDeduplicatedString(e.TaskName);
            WriteDeduplicatedString(e.ProjectFile);
            WriteDeduplicatedString(e.TaskFile);
        }

        private void Write(TaskFinishedEventArgs e)
        {
            Write(BinaryLogRecordKind.TaskFinished);
            WriteBuildEventArgsFields(e);
            Write(e.Succeeded);
            WriteDeduplicatedString(e.TaskName);
            WriteDeduplicatedString(e.ProjectFile);
            WriteDeduplicatedString(e.TaskFile);
        }

        private void Write(BuildErrorEventArgs e)
        {
            Write(BinaryLogRecordKind.Error);
            WriteBuildEventArgsFields(e);
            WriteDeduplicatedString(e.Subcategory);
            WriteDeduplicatedString(e.Code);
            WriteDeduplicatedString(e.File);
            WriteDeduplicatedString(e.ProjectFile);
            Write(e.LineNumber);
            Write(e.ColumnNumber);
            Write(e.EndLineNumber);
            Write(e.EndColumnNumber);
        }

        private void Write(BuildWarningEventArgs e)
        {
            Write(BinaryLogRecordKind.Warning);
            WriteBuildEventArgsFields(e);
            WriteDeduplicatedString(e.Subcategory);
            WriteDeduplicatedString(e.Code);
            WriteDeduplicatedString(e.File);
            WriteDeduplicatedString(e.ProjectFile);
            Write(e.LineNumber);
            Write(e.ColumnNumber);
            Write(e.EndLineNumber);
            Write(e.EndColumnNumber);
        }

        private void Write(BuildMessageEventArgs e)
        {
            if (e is TaskParameterEventArgs taskParameter)
            {
                Write(taskParameter);
                return;
            }

            if (e is CriticalBuildMessageEventArgs criticalBuildMessage)
            {
                Write(criticalBuildMessage);
                return;
            }

            if (e is TaskCommandLineEventArgs taskCommandLine)
            {
                Write(taskCommandLine);
                return;
            }

            if (e is ProjectImportedEventArgs projectImported)
            {
                Write(projectImported);
                return;
            }

            if (e is TargetSkippedEventArgs targetSkipped)
            {
                Write(targetSkipped);
                return;
            }

            if (e is PropertyReassignmentEventArgs propertyReassignment)
            {
                Write(propertyReassignment);
                return;
            }

            if (e is UninitializedPropertyReadEventArgs uninitializedPropertyRead)
            {
                Write(uninitializedPropertyRead);
                return;
            }

            if (e is EnvironmentVariableReadEventArgs environmentVariableRead)
            {
                Write(environmentVariableRead);
                return;
            }

            if (e is PropertyInitialValueSetEventArgs propertyInitialValueSet)
            {
                Write(propertyInitialValueSet);
                return;
            }

            Write(BinaryLogRecordKind.Message);
            WriteMessageFields(e);
        }

        private void Write(ProjectImportedEventArgs e)
        {
            Write(BinaryLogRecordKind.ProjectImported);
            WriteMessageFields(e);
            Write(e.ImportIgnored);
            WriteDeduplicatedString(e.ImportedProjectFile);
            WriteDeduplicatedString(e.UnexpandedProject);
        }

        private void Write(TargetSkippedEventArgs e)
        {
            Write(BinaryLogRecordKind.TargetSkipped);
            WriteMessageFields(e);
            WriteDeduplicatedString(e.TargetFile);
            WriteDeduplicatedString(e.TargetName);
            WriteDeduplicatedString(e.ParentTarget);
            Write((int)e.BuildReason);
        }

        private void Write(CriticalBuildMessageEventArgs e)
        {
            Write(BinaryLogRecordKind.CriticalBuildMessage);
            WriteMessageFields(e);
        }

        private void Write(PropertyReassignmentEventArgs e)
        {
            Write(BinaryLogRecordKind.PropertyReassignment);
            WriteMessageFields(e);
            WriteDeduplicatedString(e.PropertyName);
            WriteDeduplicatedString(e.PreviousValue);
            WriteDeduplicatedString(e.NewValue);
            WriteDeduplicatedString(e.Location);
        }

        private void Write(UninitializedPropertyReadEventArgs e)
        {
            Write(BinaryLogRecordKind.UninitializedPropertyRead);
            WriteMessageFields(e);
            WriteDeduplicatedString(e.PropertyName);
        }

        private void Write(PropertyInitialValueSetEventArgs e)
        {
            Write(BinaryLogRecordKind.PropertyInitialValueSet);
            WriteMessageFields(e);
            WriteDeduplicatedString(e.PropertyName);
            WriteDeduplicatedString(e.PropertyValue);
            WriteDeduplicatedString(e.PropertySource);
        }

        private void Write(EnvironmentVariableReadEventArgs e)
        {
            Write(BinaryLogRecordKind.EnvironmentVariableRead);
            WriteMessageFields(e);
            WriteDeduplicatedString(e.EnvironmentVariableName);
        }

        private void Write(TaskCommandLineEventArgs e)
        {
            Write(BinaryLogRecordKind.TaskCommandLine);
            WriteMessageFields(e);
            WriteDeduplicatedString(e.CommandLine);
            WriteDeduplicatedString(e.TaskName);
        }

        private void Write(TaskParameterEventArgs e)
        {
            Write(BinaryLogRecordKind.TaskParameter);
            WriteMessageFields(e, writeMessage: false);
            Write((int)e.Kind);
            WriteDeduplicatedString(e.ItemType);
            WriteTaskItemList(e.Items, e.LogItemMetadata);
        }

        private void WriteBuildEventArgsFields(BuildEventArgs e, bool writeMessage = true)
        {
            var flags = GetBuildEventArgsFieldFlags(e, writeMessage);
            Write((int)flags);
            WriteBaseFields(e, flags);
        }

        private void WriteBaseFields(BuildEventArgs e, BuildEventArgsFieldFlags flags)
        {
            if ((flags & BuildEventArgsFieldFlags.Message) != 0)
            {
                WriteDeduplicatedString(e.Message);
            }

            if ((flags & BuildEventArgsFieldFlags.BuildEventContext) != 0)
            {
                Write(e.BuildEventContext);
            }

            if ((flags & BuildEventArgsFieldFlags.ThreadId) != 0)
            {
                Write(e.ThreadId);
            }

            if ((flags & BuildEventArgsFieldFlags.HelpHeyword) != 0)
            {
                WriteDeduplicatedString(e.HelpKeyword);
            }

            if ((flags & BuildEventArgsFieldFlags.SenderName) != 0)
            {
                WriteDeduplicatedString(e.SenderName);
            }

            if ((flags & BuildEventArgsFieldFlags.Timestamp) != 0)
            {
                Write(e.Timestamp);
            }
        }

        private void WriteMessageFields(BuildMessageEventArgs e, bool writeMessage = true)
        {
            var flags = GetBuildEventArgsFieldFlags(e, writeMessage);
            flags = GetMessageFlags(e, flags);

            Write((int)flags);

            WriteBaseFields(e, flags);

            if ((flags & BuildEventArgsFieldFlags.Subcategory) != 0)
            {
                WriteDeduplicatedString(e.Subcategory);
            }

            if ((flags & BuildEventArgsFieldFlags.Code) != 0)
            {
                WriteDeduplicatedString(e.Code);
            }

            if ((flags & BuildEventArgsFieldFlags.File) != 0)
            {
                WriteDeduplicatedString(e.File);
            }

            if ((flags & BuildEventArgsFieldFlags.ProjectFile) != 0)
            {
                WriteDeduplicatedString(e.ProjectFile);
            }

            if ((flags & BuildEventArgsFieldFlags.LineNumber) != 0)
            {
                Write(e.LineNumber);
            }

            if ((flags & BuildEventArgsFieldFlags.ColumnNumber) != 0)
            {
                Write(e.ColumnNumber);
            }

            if ((flags & BuildEventArgsFieldFlags.EndLineNumber) != 0)
            {
                Write(e.EndLineNumber);
            }

            if ((flags & BuildEventArgsFieldFlags.EndColumnNumber) != 0)
            {
                Write(e.EndColumnNumber);
            }

            Write((int)e.Importance);
        }

        private static BuildEventArgsFieldFlags GetMessageFlags(BuildMessageEventArgs e, BuildEventArgsFieldFlags flags)
        {
            if (e.Subcategory != null)
            {
                flags |= BuildEventArgsFieldFlags.Subcategory;
            }

            if (e.Code != null)
            {
                flags |= BuildEventArgsFieldFlags.Code;
            }

            if (e.File != null)
            {
                flags |= BuildEventArgsFieldFlags.File;
            }

            if (e.ProjectFile != null)
            {
                flags |= BuildEventArgsFieldFlags.ProjectFile;
            }

            if (e.LineNumber != 0)
            {
                flags |= BuildEventArgsFieldFlags.LineNumber;
            }

            if (e.ColumnNumber != 0)
            {
                flags |= BuildEventArgsFieldFlags.ColumnNumber;
            }

            if (e.EndLineNumber != 0)
            {
                flags |= BuildEventArgsFieldFlags.EndLineNumber;
            }

            if (e.EndColumnNumber != 0)
            {
                flags |= BuildEventArgsFieldFlags.EndColumnNumber;
            }

            return flags;
        }

        private static BuildEventArgsFieldFlags GetBuildEventArgsFieldFlags(BuildEventArgs e, bool writeMessage = true)
        {
            var flags = BuildEventArgsFieldFlags.None;
            if (e.BuildEventContext != null)
            {
                flags |= BuildEventArgsFieldFlags.BuildEventContext;
            }

            if (e.HelpKeyword != null)
            {
                flags |= BuildEventArgsFieldFlags.HelpHeyword;
            }

            if (writeMessage)
            {
                flags |= BuildEventArgsFieldFlags.Message;
            }

            // no need to waste space for the default sender name
            if (e.SenderName != null && e.SenderName != "MSBuild")
            {
                flags |= BuildEventArgsFieldFlags.SenderName;
            }

            if (e.ThreadId > 0)
            {
                flags |= BuildEventArgsFieldFlags.ThreadId;
            }

            if (e.Timestamp != default(DateTime))
            {
                flags |= BuildEventArgsFieldFlags.Timestamp;
            }

            return flags;
        }

        private readonly List<object> reusableItemsList = new List<object>();

        private void WriteTaskItemList(IEnumerable items, bool writeMetadata = true)
        {
            if (items == null)
            {
                Write(false);
                return;
            }

            // For target outputs bypass copying of all items to save on performance.
            // The proxy creates a deep clone of each item to protect against writes,
            // but since we're not writing we don't need the deep cloning.
            // Additionally, it is safe to access the underlying List<ITaskItem> as it's allocated
            // in a single location and noboby else mutates it after that:
            // https://github.com/dotnet/msbuild/blob/f0eebf2872d76ab0cd43fdc4153ba636232b222f/src/Build/BackEnd/Components/RequestBuilder/TargetEntry.cs#L564
            if (items is TargetLoggingContext.TargetOutputItemsInstanceEnumeratorProxy proxy)
            {
                items = proxy.BackingItems;
            }

            int count;

            if (items is ICollection arrayList)
            {
                count = arrayList.Count;
            }
            else if (items is ICollection<ITaskItem> genericList)
            {
                count = genericList.Count;
            }
            else
            {
                // enumerate only once
                foreach (var item in items)
                {
                    if (item != null)
                    {
                        reusableItemsList.Add(item);
                    }
                }

                items = reusableItemsList;
                count = reusableItemsList.Count;
            }

            Write(count);

            foreach (var item in items)
            {
                if (item is ITaskItem taskItem)
                {
                    Write(taskItem, writeMetadata);
                }
                else
                {
                    WriteDeduplicatedString(item?.ToString() ?? ""); // itemspec
                    Write(0); // no metadata
                }
            }

            reusableItemsList.Clear();
        }

        private readonly List<(string Key, ITaskItem Value)> reusableProjectItemList = new List<(string, ITaskItem)>();

        private void WriteProjectItems(IEnumerable items)
        {
            if (items == null)
            {
                Write(0);
                return;
            }

            foreach (var item in items)
            {
                string itemType = default;

                if (item is IItem iitem)
                {
                    itemType = iitem.Key;
                }
                else if (item is DictionaryEntry dictionaryEntry)
                {
                    itemType = dictionaryEntry.Key as string;
                }

                if (string.IsNullOrEmpty(itemType) || !(item is ITaskItem taskItem))
                {
                    continue;
                }

                reusableProjectItemList.Add((itemType, taskItem));
            }

            var groups = reusableProjectItemList
                .GroupBy(entry => entry.Key, entry => entry.Value)
                .ToArray();

            Write(groups.Length);

            foreach (var group in groups)
            {
                WriteDeduplicatedString(group.Key);
                WriteTaskItemList(group);
            }

            reusableProjectItemList.Clear();
        }

        private void Write(ITaskItem item, bool writeMetadata = true)
        {
            WriteDeduplicatedString(item.ItemSpec);
            if (!writeMetadata)
            {
                Write((byte)0);
                return;
            }

            // WARNING: Can't use AddRange here because CopyOnWriteDictionary in Microsoft.Build.Utilities.v4.0.dll
            // is broken. Microsoft.Build.Utilities.v4.0.dll loads from the GAC by XAML markup tooling and it's
            // implementation doesn't work with AddRange because AddRange special-cases ICollection<T> and
            // CopyOnWriteDictionary doesn't implement it properly.
            foreach (var kvp in item.EnumerateMetadata())
            {
                nameValueListBuffer.Add(kvp);
            }

            if (nameValueListBuffer.Count > 1)
            {
                nameValueListBuffer.Sort((l, r) => StringComparer.OrdinalIgnoreCase.Compare(l.Key, r.Key));
            }

            WriteNameValueList();

            nameValueListBuffer.Clear();
        }

        private void WriteProperties(IEnumerable properties)
        {
            if (properties == null)
            {
                Write(0);
                return;
            }

            foreach (var item in properties)
            {
                if (item is IProperty property && !string.IsNullOrEmpty(property.Name))
                {
                    nameValueListBuffer.Add(new KeyValuePair<string, string>(property.Name, property.EvaluatedValue ?? string.Empty));
                }
                else if (item is DictionaryEntry dictionaryEntry && dictionaryEntry.Key is string key && !string.IsNullOrEmpty(key))
                {
                    nameValueListBuffer.Add(new KeyValuePair<string, string>(key, dictionaryEntry.Value as string ?? string.Empty));
                }
            }

            WriteNameValueList();

            nameValueListBuffer.Clear();
        }

        private void Write(BuildEventContext buildEventContext)
        {
            Write(buildEventContext.NodeId);
            Write(buildEventContext.ProjectContextId);
            Write(buildEventContext.TargetId);
            Write(buildEventContext.TaskId);
            Write(buildEventContext.SubmissionId);
            Write(buildEventContext.ProjectInstanceId);
            Write(buildEventContext.EvaluationId);
        }

        private void Write(IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (keyValuePairs != null)
            {
                foreach (var kvp in keyValuePairs)
                {
                    nameValueListBuffer.Add(kvp);
                }
            }

            WriteNameValueList();

            nameValueListBuffer.Clear();
        }

        private void WriteNameValueList()
        {
            if (nameValueListBuffer.Count == 0)
            {
                Write((byte)0);
                return;
            }

            HashKey hash = HashAllStrings(nameValueListBuffer);
            if (!nameValueListHashes.TryGetValue(hash, out var recordId))
            {
                recordId = nameValueRecordId;
                nameValueListHashes[hash] = nameValueRecordId;

                WriteNameValueListRecord();

                nameValueRecordId += 1;
            }

            Write(recordId);
        }

        /// <summary>
        /// In the middle of writing the current record we may discover that we want to write another record
        /// preceding the current one, specifically the list of names and values we want to reuse in the
        /// future. As we are writing the current record to a MemoryStream first, it's OK to temporarily
        /// switch to the direct underlying stream and write the NameValueList record first.
        /// When the current record is done writing, the MemoryStream will flush to the underlying stream
        /// and the current record will end up after the NameValueList record, as desired.
        /// </summary>
        private void WriteNameValueListRecord()
        {
            // Switch the binaryWriter used by the Write* methods to the direct underlying stream writer.
            // We want this record to precede the record we're currently writing to currentRecordWriter
            // which is backed by a MemoryStream buffer
            using var redirectionScope = RedirectWritesToOriginalWriter();

            Write(BinaryLogRecordKind.NameValueList);
            Write(nameValueIndexListBuffer.Count);
            for (int i = 0; i < nameValueListBuffer.Count; i++)
            {
                var kvp = nameValueIndexListBuffer[i];
                Write(kvp.Key);
                Write(kvp.Value);
            }
        }

        /// <summary>
        /// Compute the total hash of all items in the nameValueList
        /// while simultaneously filling the nameValueIndexListBuffer with the individual
        /// hashes of the strings, mirroring the strings in the original nameValueList.
        /// This helps us avoid hashing strings twice (once to hash the string individually
        /// and the second time when hashing it as part of the nameValueList)
        /// </summary>
        private HashKey HashAllStrings(List<KeyValuePair<string, string>> nameValueList)
        {
            HashKey hash = new HashKey();

            nameValueIndexListBuffer.Clear();

            for (int i = 0; i < nameValueList.Count; i++)
            {
                var kvp = nameValueList[i];
                var (keyIndex, keyHash) = HashString(kvp.Key);
                var (valueIndex, valueHash) = HashString(kvp.Value);
                hash = hash.Add(keyHash);
                hash = hash.Add(valueHash);
                nameValueIndexListBuffer.Add(new KeyValuePair<int, int>(keyIndex, valueIndex));
            }

            return hash;
        }

        private void Write(BinaryLogRecordKind kind)
        {
            Write((int)kind);
        }

        private void Write(int value)
        {
            BinaryWriterExtensions.Write7BitEncodedInt(binaryWriter, value);
        }

        private void Write(long value)
        {
            binaryWriter.Write(value);
        }

        private void Write(byte[] bytes)
        {
            binaryWriter.Write(bytes);
        }

        private void Write(byte b)
        {
            binaryWriter.Write(b);
        }

        private void Write(bool boolean)
        {
            binaryWriter.Write(boolean);
        }

        private void WriteDeduplicatedString(string text)
        {
            var (recordId, _) = HashString(text);
            Write(recordId);
        }

        /// <summary>
        /// Hash the string and write a String record if not already hashed.
        /// </summary>
        /// <returns>Returns the string record index as well as the hash.</returns>
        private (int index, HashKey hash) HashString(string text)
        {
            if (text == null)
            {
                return (0, default);
            }
            else if (text.Length == 0)
            {
                return (1, default);
            }

            var hash = new HashKey(text);
            if (!stringHashes.TryGetValue(hash, out var recordId))
            {
                recordId = stringRecordId;
                stringHashes[hash] = stringRecordId;

                WriteStringRecord(text);

                stringRecordId += 1;
            }

            return (recordId, hash);
        }

        private void WriteStringRecord(string text)
        {
            using var redirectionScope = RedirectWritesToOriginalWriter();

            Write(BinaryLogRecordKind.String);
            binaryWriter.Write(text);
        }

        private void Write(DateTime timestamp)
        {
            binaryWriter.Write(timestamp.Ticks);
            Write((int)timestamp.Kind);
        }

        private void Write(TimeSpan timeSpan)
        {
            binaryWriter.Write(timeSpan.Ticks);
        }

        private void Write(EvaluationLocation item)
        {
            WriteDeduplicatedString(item.ElementName);
            WriteDeduplicatedString(item.ElementDescription);
            WriteDeduplicatedString(item.EvaluationPassDescription);
            WriteDeduplicatedString(item.File);
            Write((int)item.Kind);
            Write((int)item.EvaluationPass);

            Write(item.Line.HasValue);
            if (item.Line.HasValue)
            {
                Write(item.Line.Value);
            }

            Write(item.Id);
            Write(item.ParentId.HasValue);
            if (item.ParentId.HasValue)
            {
                Write(item.ParentId.Value);
            }
        }

        private void Write(ProfiledLocation e)
        {
            Write(e.NumberOfHits);
            Write(e.ExclusiveTime);
            Write(e.InclusiveTime);
        }

        internal readonly struct HashKey : IEquatable<HashKey>
        {
            private readonly ulong value;

            private HashKey(ulong i)
            {
                value = i;
            }

            public HashKey(string text)
            {
                if (text == null)
                {
                    value = 0;
                }
                else
                {
                    value = FnvHash64.GetHashCode(text);
                }
            }

            public static HashKey Combine(HashKey left, HashKey right)
            {
                return new HashKey(FnvHash64.Combine(left.value, right.value));
            }

            public HashKey Add(HashKey other) => Combine(this, other);

            public bool Equals(HashKey other)
            {
                return value == other.value;
            }

            public override bool Equals(object obj)
            {
                if (obj is HashKey other)
                {
                    return Equals(other);
                }

                return false;
            }

            public override int GetHashCode()
            {
                return unchecked((int)value);
            }

            public override string ToString()
            {
                return value.ToString();
            }
        }

        internal static class FnvHash64
        {
            public const ulong Offset = 14695981039346656037;
            public const ulong Prime = 1099511628211;

            public static ulong GetHashCode(string text)
            {
                ulong hash = Offset;

                unchecked
                {
                    for (int i = 0; i < text.Length; i++)
                    {
                        char ch = text[i];
                        hash = (hash ^ ch) * Prime;
                    }
                }

                return hash;
            }

            public static ulong Combine(ulong left, ulong right)
            {
                unchecked
                {
                    return (left ^ right) * Prime;
                }
            }
        }
    }
}
