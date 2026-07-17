// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#nullable enable

using System.ComponentModel.Composition;

using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;

using MonoDevelop.MSBuild.Language.Expressions;

namespace MonoDevelop.MSBuild.Editor.Classification
{
	/// <summary>
	/// Maps MSBuild syntax constructs to tags based on built-in theme-aware classification types,
	/// for use by <see cref="MSBuildClassificationTagger"/> when the VS TextMate service is unavailable.
	/// </summary>
	[Export]
	sealed class MSBuildClassificationTypeMap
	{
		/// <summary>
		/// Creates the map, resolving the built-in classification types from the registry.
		/// </summary>
		/// <param name="classificationTypeRegistry">The editor's classification type registry.</param>
		[ImportingConstructor]
		public MSBuildClassificationTypeMap (IClassificationTypeRegistryService classificationTypeRegistry)
		{
			ElementName = CreateTag (classificationTypeRegistry, PredefinedClassificationTypeNames.MarkupNode);
			AttributeName = CreateTag (classificationTypeRegistry, PredefinedClassificationTypeNames.MarkupAttribute);
			AttributeValue = CreateTag (classificationTypeRegistry, PredefinedClassificationTypeNames.String);
			Comment = CreateTag (classificationTypeRegistry, PredefinedClassificationTypeNames.Comment);
			CDataSection = CreateTag (classificationTypeRegistry, PredefinedClassificationTypeNames.Literal);
			ProcessingInstruction = CreateTag (classificationTypeRegistry, PredefinedClassificationTypeNames.PreprocessorKeyword);
			ExpressionName = CreateTag (classificationTypeRegistry, PredefinedClassificationTypeNames.Keyword);
			ExpressionDelimiter = CreateTag (classificationTypeRegistry, PredefinedClassificationTypeNames.Operator);
			FunctionName = CreateTag (classificationTypeRegistry, PredefinedClassificationTypeNames.Identifier);
			BoolLiteral = CreateTag (classificationTypeRegistry, PredefinedClassificationTypeNames.Keyword);
			NumberLiteral = CreateTag (classificationTypeRegistry, PredefinedClassificationTypeNames.Number);
		}

		public ClassificationTag ElementName { get; }
		public ClassificationTag AttributeName { get; }
		public ClassificationTag AttributeValue { get; }
		public ClassificationTag Comment { get; }
		public ClassificationTag CDataSection { get; }
		public ClassificationTag ProcessingInstruction { get; }
		public ClassificationTag ExpressionName { get; }
		public ClassificationTag ExpressionDelimiter { get; }
		public ClassificationTag FunctionName { get; }
		public ClassificationTag BoolLiteral { get; }
		public ClassificationTag NumberLiteral { get; }

		/// <summary>
		/// Gets the classification tag for an MSBuild expression node, or null if the node is not classified.
		/// </summary>
		/// <param name="node">The expression node.</param>
		/// <returns>The tag for the node's whole span, or null to leave the span unclassified.</returns>
		public ClassificationTag? GetTagForExpressionNode (ExpressionNode node)
			=> node switch {
				ExpressionPropertyName => ExpressionName,
				ExpressionItemName => ExpressionName,
				ExpressionFunctionName => FunctionName,
				ExpressionArgumentBool => BoolLiteral,
				ExpressionArgumentInt => NumberLiteral,
				ExpressionArgumentFloat => NumberLiteral,
				ExpressionArgumentString => AttributeValue,
				_ => null
			};

		/// <summary>
		/// Creates a classification tag for a built-in classification type, falling back to plain text if it is not registered.
		/// </summary>
		/// <param name="classificationTypeRegistry">The editor's classification type registry.</param>
		/// <param name="classificationTypeName">The name of the classification type.</param>
		/// <returns>A tag for the resolved classification type.</returns>
		static ClassificationTag CreateTag (IClassificationTypeRegistryService classificationTypeRegistry, string classificationTypeName)
		{
			IClassificationType? classificationType =
				classificationTypeRegistry.GetClassificationType (classificationTypeName)
				?? classificationTypeRegistry.GetClassificationType (PredefinedClassificationTypeNames.Text);
			return new ClassificationTag (classificationType);
		}
	}
}
