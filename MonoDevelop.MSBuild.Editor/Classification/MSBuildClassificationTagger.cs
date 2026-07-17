// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;

using MonoDevelop.MSBuild.Language.Expressions;
using MonoDevelop.Xml.Dom;
using MonoDevelop.Xml.Editor.Parsing;
using MonoDevelop.Xml.Logging;

namespace MonoDevelop.MSBuild.Editor.Classification
{
	/// <summary>
	/// Classifies MSBuild files using the extension's own XML and expression parsers.
	/// Used as a fallback when the VS TextMate service is unavailable (e.g. VS 2026, issue #279).
	/// </summary>
	sealed partial class MSBuildClassificationTagger : ITagger<ClassificationTag>, IDisposable
	{
		readonly ITextBuffer buffer;
		readonly XmlBackgroundParser parser;
		readonly MSBuildClassificationTypeMap typeMap;
		readonly JoinableTaskContext joinableTaskContext;
		readonly ILogger logger;

		bool isDisposed;

		/// <summary>
		/// Creates a classification tagger for the buffer.
		/// </summary>
		/// <param name="buffer">The text buffer to tag.</param>
		/// <param name="parserProvider">Provider used to obtain the per-buffer XML background parser.</param>
		/// <param name="typeMap">Shared map from MSBuild syntax constructs to classification tags.</param>
		/// <param name="joinableTaskContext">Used to raise <see cref="TagsChanged"/> on the main thread.</param>
		/// <param name="logger">Logger for exception reporting.</param>
		public MSBuildClassificationTagger (ITextBuffer buffer, XmlParserProvider parserProvider, MSBuildClassificationTypeMap typeMap, JoinableTaskContext joinableTaskContext, ILogger logger)
		{
			this.buffer = buffer;
			this.typeMap = typeMap;
			this.joinableTaskContext = joinableTaskContext;
			this.logger = logger;

			parser = parserProvider.GetParser (buffer);
			parser.ParseCompleted += ParseCompleted;
			buffer.ContentTypeChanged += BufferContentTypeChanged;
		}

		public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

		void ParseCompleted (object? sender, ParseCompletedEventArgs<XmlParseResult> args)
		{
			joinableTaskContext.Factory.Run (async delegate {
				await joinableTaskContext.Factory.SwitchToMainThreadAsync ();
				//FIXME: figure out which spans changed, if any, and only invalidate those
				TagsChanged?.Invoke (this, new SnapshotSpanEventArgs (new SnapshotSpan (args.Snapshot, 0, args.Snapshot.Length)));
			});
		}

		void RaiseTagsChanged ()
		{
			ITextSnapshot snapshot = buffer.CurrentSnapshot;
			TagsChanged?.Invoke (this, new SnapshotSpanEventArgs (new SnapshotSpan (snapshot, 0, snapshot.Length)));
		}

		void BufferContentTypeChanged (object? sender, ContentTypeChangedEventArgs e)
		{
			// if the buffer is no longer an MSBuild buffer, discard the tagger.
			// it will be recreated if needed anyway.
			if (!e.AfterContentType.IsOfType (MSBuildContentType.Name)) {
				Dispose ();
			}
		}

		public void Dispose ()
		{
			if (isDisposed) {
				return;
			}
			isDisposed = true;
			parser.ParseCompleted -= ParseCompleted;
			buffer.ContentTypeChanged -= BufferContentTypeChanged;
			buffer.Properties.RemoveProperty (typeof (MSBuildClassificationTagger));
		}

		/// <summary>
		/// Computes classification tags for the requested snapshot spans from the most recent XML parse result.
		/// </summary>
		/// <param name="spans">The spans for which tags are requested.</param>
		/// <returns>Classification tag spans intersecting the requested spans.</returns>
		public IEnumerable<ITagSpan<ClassificationTag>> GetTags (NormalizedSnapshotSpanCollection spans)
			=> logger.InvokeAndLogExceptions (() => GetTagsInternal (spans));

		IEnumerable<ITagSpan<ClassificationTag>> GetTagsInternal (NormalizedSnapshotSpanCollection spans)
		{
			List<ITagSpan<ClassificationTag>> results = new ();

			if (spans.Count == 0) {
				return results;
			}

			ITextSnapshot targetSnapshot = spans[0].Snapshot;

			Task<XmlParseResult> parseTask = parser.GetOrProcessAsync (targetSnapshot, CancellationToken.None);

			XmlParseResult? parseResult;
			if (parseTask.IsCompleted) {
				#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
				parseResult = parseTask.Result;
				#pragma warning restore VSTHRD002
			} else {
				// use the most recent completed parse for now, and raise TagsChanged when the parse
				// for the requested snapshot completes so the tags get recomputed
				parseTask.ContinueWith (t => RaiseTagsChanged (), TaskScheduler.Default).LogTaskExceptionsAndForget (logger);
				parseResult = parser.LastOutput;
			}

			if (parseResult is null) {
				return results;
			}

			ITextSnapshot parseSnapshot = parseResult.TextSnapshot;
			List<ClassificationRun> runs = new ();

			foreach (SnapshotSpan taggingSpan in spans) {
				runs.Clear ();

				// the parse may be from an older snapshot, so clamp the requested range to its length.
				// the tag spans will be mapped back to the requested snapshot below.
				int rangeStart = Math.Min (taggingSpan.Start.Position, parseSnapshot.Length);
				int rangeEnd = Math.Min (taggingSpan.End.Position, parseSnapshot.Length);
				TextSpan range = TextSpan.FromBounds (rangeStart, rangeEnd);

				CollectRuns (parseResult.XDocument, range, runs);

				foreach (ClassificationRun run in runs) {
					if (run.Length == 0 || run.Start < 0 || run.End > parseSnapshot.Length) {
						continue;
					}

					SnapshotSpan runSpan = new SnapshotSpan (parseSnapshot, run.Start, run.Length);

					// if the parse was from an older snapshot, map the positions into the requested snapshot.
					// EdgeExclusive means freshly typed characters don't inherit stale classifications.
					if (parseSnapshot != targetSnapshot) {
						ITrackingSpan trackingSpan = parseSnapshot.CreateTrackingSpan (runSpan, SpanTrackingMode.EdgeExclusive);
						runSpan = trackingSpan.GetSpan (targetSnapshot);
						if (runSpan.Length == 0) {
							continue;
						}
					}

					if (runSpan.IntersectsWith (taggingSpan)) {
						results.Add (new TagSpan<ClassificationTag> (runSpan, run.Tag));
					}
				}
			}

			return results;
		}

		/// <summary>
		/// Collects classification runs for all nodes in the container that intersect the range.
		/// </summary>
		/// <param name="container">The XML container whose child nodes are classified.</param>
		/// <param name="range">The range for which runs are requested, in parse snapshot coordinates.</param>
		/// <param name="runs">The list to which runs are added.</param>
		void CollectRuns (XContainer container, TextSpan range, List<ClassificationRun> runs)
		{
			foreach (XNode node in container.Nodes) {
				if (node.OuterSpan.End < range.Start) {
					continue;
				}
				if (node.OuterSpan.Start >= range.End) {
					break;
				}

				switch (node) {
				case XElement element:
					CollectElementRuns (element, range, runs);
					break;
				case XComment comment:
					runs.Add (new ClassificationRun (comment.Span, typeMap.Comment));
					break;
				case XCData cdata:
					runs.Add (new ClassificationRun (cdata.Span, typeMap.CDataSection));
					break;
				case XProcessingInstruction processingInstruction:
					runs.Add (new ClassificationRun (processingInstruction.Span, typeMap.ProcessingInstruction));
					break;
				case XDocType docType:
					runs.Add (new ClassificationRun (docType.Span, typeMap.ProcessingInstruction));
					break;
				case XClosingTag closingTag:
					// orphaned closing tag with no matching element
					if (closingTag.IsNamed) {
						runs.Add (new ClassificationRun (closingTag.NameSpan, typeMap.ElementName));
					}
					break;
				case XText text:
					AddExpressionRuns (text.Text, text.Span.Start, fillGapsAsAttributeValue: false, runs);
					break;
				}
			}
		}

		/// <summary>
		/// Collects classification runs for an element's name tags, attributes, and child nodes.
		/// </summary>
		/// <param name="element">The element to classify.</param>
		/// <param name="range">The range for which runs are requested, in parse snapshot coordinates.</param>
		/// <param name="runs">The list to which runs are added.</param>
		void CollectElementRuns (XElement element, TextSpan range, List<ClassificationRun> runs)
		{
			if (element.Span.Intersects (range)) {
				if (element.IsNamed) {
					runs.Add (new ClassificationRun (element.NameSpan, typeMap.ElementName));
				}
				foreach (XAttribute attribute in element.Attributes) {
					if (attribute.IsNamed) {
						runs.Add (new ClassificationRun (attribute.NameSpan, typeMap.AttributeName));
					}
					if (attribute.HasValue && attribute.Value.Length > 0) {
						AddExpressionRuns (attribute.Value, attribute.ValueOffset.Value, fillGapsAsAttributeValue: true, runs);
					}
				}
			}

			CollectRuns (element, range, runs);

			if (element.ClosingTag is XClosingTag elementClosingTag && elementClosingTag.IsNamed && elementClosingTag.Span.Intersects (range)) {
				runs.Add (new ClassificationRun (elementClosingTag.NameSpan, typeMap.ElementName));
			}
		}

		/// <summary>
		/// Parses a value as an MSBuild expression and adds classification runs for the expression constructs in it.
		/// </summary>
		/// <param name="text">The value text.</param>
		/// <param name="baseOffset">The offset of the value in the parse snapshot.</param>
		/// <param name="fillGapsAsAttributeValue">Whether to classify non-expression segments as attribute value text.</param>
		/// <param name="runs">The list to which runs are added.</param>
		void AddExpressionRuns (string text, int baseOffset, bool fillGapsAsAttributeValue, List<ClassificationRun> runs)
		{
			if (text.Length == 0) {
				return;
			}

			List<ClassificationRun> expressionRuns = new ();

			try {
				ExpressionNode expression = ExpressionParser.Parse (text, ExpressionOptions.ItemsMetadataAndLists, baseOffset);

				foreach (ExpressionNode node in expression.WithAllDescendants ()) {
					switch (node) {
					case ExpressionProperty property:
						AddExpressionDelimiterRuns (property, text, baseOffset, expressionRuns);
						break;
					case ExpressionItem item:
						AddExpressionDelimiterRuns (item, text, baseOffset, expressionRuns);
						break;
					case ExpressionMetadata metadata:
						AddExpressionDelimiterRuns (metadata, text, baseOffset, expressionRuns);
						if (metadata.IsQualified && metadata.ItemName.Length > 0) {
							expressionRuns.Add (new ClassificationRun (metadata.ItemNameSpan, typeMap.ExpressionName));
						}
						if (!string.IsNullOrEmpty (metadata.MetadataName)) {
							expressionRuns.Add (new ClassificationRun (metadata.MetadataNameSpan, typeMap.ExpressionName));
						}
						break;
					default:
						if (node.Length > 0 && typeMap.GetTagForExpressionNode (node) is ClassificationTag tag) {
							expressionRuns.Add (new ClassificationRun (node.Span, tag));
						}
						break;
					}
				}
			} catch (Exception ex) {
				// the expression parser is not guaranteed to handle partially typed expressions gracefully,
				// so degrade to unclassified (or plain attribute value) rather than losing all tags for the request
				LogExpressionParserError (logger, ex);
				expressionRuns.Clear ();
			}

			if (!fillGapsAsAttributeValue) {
				runs.AddRange (expressionRuns);
				return;
			}

			expressionRuns.Sort ((a, b) => a.Start.CompareTo (b.Start));

			int position = baseOffset;
			int valueEnd = baseOffset + text.Length;
			foreach (ClassificationRun expressionRun in expressionRuns) {
				if (expressionRun.Start > position) {
					runs.Add (new ClassificationRun (position, expressionRun.Start - position, typeMap.AttributeValue));
				}
				runs.Add (expressionRun);
				position = Math.Max (position, expressionRun.End);
			}
			if (position < valueEnd) {
				runs.Add (new ClassificationRun (position, valueEnd - position, typeMap.AttributeValue));
			}
		}

		/// <summary>
		/// Adds classification runs for an expression node's delimiters, i.e. the leading <c>$(</c>, <c>@(</c> or <c>%(</c> and the trailing <c>)</c> if present.
		/// </summary>
		/// <param name="node">The property, item or metadata expression node.</param>
		/// <param name="text">The value text the node was parsed from.</param>
		/// <param name="baseOffset">The offset of the value in the parse snapshot.</param>
		/// <param name="runs">The list to which runs are added.</param>
		void AddExpressionDelimiterRuns (ExpressionNode node, string text, int baseOffset, List<ClassificationRun> runs)
		{
			if (node.Length < 2) {
				return;
			}

			runs.Add (new ClassificationRun (node.Offset, 2, typeMap.ExpressionDelimiter));

			int lastCharIndex = node.End - 1 - baseOffset;
			if (node.Length > 2 && lastCharIndex < text.Length && text[lastCharIndex] == ')') {
				runs.Add (new ClassificationRun (node.End - 1, 1, typeMap.ExpressionDelimiter));
			}
		}

		readonly struct ClassificationRun
		{
			public ClassificationRun (int start, int length, ClassificationTag tag)
			{
				Start = start;
				Length = length;
				Tag = tag;
			}

			public ClassificationRun (TextSpan span, ClassificationTag tag) : this (span.Start, span.Length, tag)
			{
			}

			public readonly int Start;
			public readonly int Length;
			public readonly ClassificationTag Tag;

			public int End => Start + Length;
		}

		[LoggerMessage (EventId = 0, Level = LogLevel.Debug, Message = "Expression parser failed on partial input, skipping expression classification")]
		static partial void LogExpressionParserError (ILogger logger, Exception ex);
	}
}
